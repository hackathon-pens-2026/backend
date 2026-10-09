# SignIt Backend

Backend **SignIt!** (Digital Campus Worker): pengajuan surat, routing persetujuan, tanda tangan QR,
reservasi fasilitas, notifikasi email, dan verifikasi publik. Satu aplikasi ASP.NET Core
(modular monolith) dengan PostgreSQL.

- Stack: ASP.NET Core (.NET 10) + EF Core/Npgsql, PostgreSQL 16, JWT RS256, QuestPDF/PdfSharp,
  QRCoder, Resend (email), Hangfire-style worker berbasis `BackgroundService`.
- Target deployment: API/worker di Azure App Service (Always On), database PostgreSQL persisten,
  frontend Next.js di Vercel. Development lokal: Docker Compose di `infrastructure/docker/`.

Panduan kerja agen/anggota tim ada di `AGENTS.md` (batas modul, aturan otorisasi, larangan
menyimpan secret, dsb). Baca sebelum mengubah kode.

---

## 1. Struktur

```text
backend/
├── Program.cs                       # komposisi aplikasi, auth, CORS, rate limit, health, OpenAPI
├── Modules/                         # fitur (namespace/folder, satu proyek)
│   ├── Authentication/              # login sesi JWT RS256, akun, assignment, reset password
│   ├── Letters/                     # draft, preview job, submit/resubmit/cancel, dokumen
│   ├── Routing/                     # katalog organisasi/fasilitas + resolver chain approval
│   ├── Workflow/                    # task keputusan (reject/revisi/defer/delegasi), query inbox
│   ├── Signatures/                  # QR per akun, evidence, PDF overlay, verifikasi publik
│   ├── Rooms/                       # reservasi fasilitas + pencegahan bentrok (exclusion constraint)
│   ├── Email/                       # webhook provider, outbox EmailDelivery, notifikasi workflow
│   ├── Templates/                   # katalog template dari file JSON
│   ├── Chat/ Inventory/             # disiapkan, belum ada implementasi
├── Infrastructure/
│   ├── Persistence/                 # AppDbContext, migrations, design-time factory
│   ├── Email/                       # adapter Resend, worker email, verifier signature Svix
│   ├── Storage/                     # LocalStorageService (S3-compatible lewat adapter nanti)
│   ├── Configuration/               # DotEnv loader (.env lokal)
│   ├── Errors/                      # SignItDomainException, problem+json handler
│   └── Llm/                         # disiapkan, belum ada implementasi
├── tests/                           # SignIt.Auth.Tests, SignIt.Email.Tests,
│                                    # SignIt.Rooms.Tests, SignIt.Signatures.Tests
├── provisioning/                    # script seed: auth, organizations, facilities (+ SQL schema)
├── appsettings.json                 # struktur + placeholder kosong (tanpa secret)
└── templates/                       # aset template surat (JSON + DOCX/PDF) — lihat catatan
```

---

## 2. Menjalankan (lokal)

Prasyarat: .NET SDK 10, Docker Desktop (untuk PostgreSQL), `dotnet tool restore` (repo memakai
local tool manifest untuk `dotnet-ef`).

```powershell
# 1. Database (di root repo)
docker compose -f infrastructure/docker/docker-compose.yml up -d database

# 2. Konfigurasi lokal: salin template lalu isi nilainya (file .env ter-ignore)
Copy-Item .env.example .env        # isi ConnectionStrings, Jwt__PrivateKeyPem, Resend keys, seed password

# 3. Migration + provisioning + jalankan
dotnet tool restore
dotnet ef database update          # DotEnv memuat .env otomatis, tanpa set env manual
dotnet run -- --provision-auth provisioning.local.json
dotnet run --launch-profile https  # http://localhost:5217 / https://localhost:7262
```

Alternatif konfigurasi tanpa `.env`: `dotnet user-secrets` (lihat §4) atau environment variable.

### Docker penuh (DB + API + frontend)
Compose di `infrastructure/docker/` membaca `secrets.env` (nilai sensitif) dan `.env`
(override seperti `BACKEND_PORT`) — keduanya ter-ignore git. Lihat `infrastructure/README.md`.

---

## 3. Database & migrasi

PostgreSQL 16 via Docker, host port **5434** (internal 5432). Connection string default lokal:
`Host=localhost;Port=5434;Database=hackathondb;Username=appuser;Password=apppassword`.

Migration yang ada (`Infrastructure/Persistence/Migrations/`):

| Migration | Isi |
|---|---|
| `InitialAuthentication` | akun, assignment, sesi, refresh/reset token, audit, budget email |
| `AddSignaturesAndWorkflow` | QR, evidence, signing attempt, letter request/revision/participant, task, delegasi, dokumen, audit log |
| `AddOrganizationsAndFacilities` | organisasi (routing), fasilitas + resource |
| `AddEmailWebhookEvents` | `email_provider_events`, `email_suppressions` |
| `AddLetterPreviewJobs` | job preview PDF |
| `AddRoomReservations` | `room_reservations` + extension `btree_gist` + **exclusion constraint** anti-bentrok |
| `AddEmailDeliveries` | outbox email generik (dedup key, status, retry) |

Menambah migration (wajib ikuti lokasi/nampa folder agar konsisten):

```powershell
dotnet ef migrations add <Nama> `
  --output-dir Infrastructure/Persistence/Migrations `
  --namespace SignIt.Infrastructure.Persistence.Migrations
```

`AppDbContextFactory` (design-time) juga memuat `.env`, jadi `dotnet ef` tidak butuh env manual
selama `.env` ada. Cek konsistensi model: `dotnet ef migrations has-pending-model-changes`.

---

## 4. Konfigurasi & secret

Prioritas (yang ada menang): **environment variable > `.env`/user-secrets > appsettings**.
`appsettings.json` hanya memuat struktur dan nilai kosong; validasi menolak start jika secret
wajib tidak diisi. Jangan pernah commit secret.

| Variabel | Kebutuhan |
|---|---|
| `ConnectionStrings__DefaultConnection` | PostgreSQL (dev `localhost:5434`) |
| `Jwt__Issuer`, `Jwt__Audience`, `Jwt__KeyId` | konsisten di semua replika (`SignIt.Api` / `SignIt.Bff` / `signit-auth-v1`) |
| `Jwt__PrivateKeyPem` | private key RSA PEM ≥ 2048-bit (secret) |
| `Auth__*` | opsional; default aman di `appsettings.json` (lockout, rate limit, sesi, iterasi hash) |
| `Email__WorkerEnabled` | default `false`; aktifkan eksplisit agar worker mengirim |
| `Email__SandboxMode`, `Email__RecipientAllowlist__0..N` | wajib di non-production; hanya kirim ke alamat allowlist |
| `Email__From`, `Email__ReplyTo` | pengirim domain terverifikasi + mailbox bantuan |
| `Email__ResetPasswordUrl` | URL HTTPS halaman reset (token dibaca dari fragment `#token=`) |
| `Email__AppBaseUrl`, `Email__LetterPathTemplate` | tautan email ke halaman surat (default `http://localhost:3000` + `/surat/{id}`) |
| `Email__ReminderCooldownHours`, `Email__MaxRemindersPerTask`, `Email__ReminderPollSeconds` | kebijakan reminder SLA |
| `Email__DailyBudget`, `Email__MonthlyBudget`, `Email__PollSeconds`, `Email__MaxAttempts`, `Email__HttpTimeoutSeconds` | operasional worker |
| `Resend__ApiKey`, `Resend__WebhookSecret` | kredensial provider + secret signature webhook |
| `Resend__WebhookToleranceSeconds` | toleransi timestamp signature (default 300) |
| `Preview__Enabled/PollSeconds/RenderTimeoutSeconds/LeaseSeconds` | worker render preview PDF |
| `Workflow__SlaDays` | SLA default tugas (DueAt) untuk reminder/overdue |
| `Storage__RootDirectory` | root penyimpanan file (default `.data/storage`) |
| `DataProtection__KeyDirectory` | key ring Data Protection (persisten) |
| `Cors__AllowedOrigins__0..N`, `Proxy__KnownProxies__0..N` | origin frontend exact + proxy tepercaya |

Contoh user-secrets:

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5434;Database=hackathondb;Username=<user>;Password=<secret>"
dotnet user-secrets set "Jwt:PrivateKeyPem" (Get-Content -Raw ".data/auth-signing.pem")
```

Generate key JWT lokal:
`openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out .data/auth-signing.pem`.

---

## 5. Provisioning data awal

Akun dibuat tim lewat CLI (tanpa registrasi/panel akun):

```powershell
Copy-Item provisioning/auth.example.json provisioning.local.json   # sesuaikan akun/assignment
dotnet run -- --provision-auth provisioning.local.json
```

Password akun diambil dari environment variable yang dirujuk manifest (`SIGNIT_SEED_*`).
Idempoten: akun lama tidak direset/diduplikasi.

Script SQL untuk master data (jalankan via `psql` pada database dev):

- `provisioning/organizations.sql` — organisasi + scope untuk routing.
- `provisioning/facilities.sql` — fasilitas (PS/SAW/D3/D4/lapangan) + resource ruangan.
- `provisioning/auth-schema.sql` — referensi skema auth (dipakai jika perlu seed manual).

**Catatan aset template**: schema `{typeId}.json` dan dokumen sumber `*.docx` disimpan di
`backend/templates/` (dev juga menerima fallback `<repo-root>/templates/`). Katalog `GET /templates`
membaca file tersebut. Pipeline pratinjau **juga** memerlukan pasangan `{typeId}.layout.json`
(schema blok `pageBreak/heading/paragraph/right/table`, lihat `docs/template-renderer.md` dan
`PdfSharpLetterTemplateRenderer.Version = "signit-pdfsharp-v1"`); selama file layout belum tersedia,
`POST /letters/{id}/preview` gagal. Pastikan juga versi schema sesuai dengan yang diharapkan renderer.

---

## 6. Endpoint (`/api/v1`)

Semua endpoint butuh login kecuali yang ditandai publik. Aksi tulis mutasi workflow memerlukan
header `Idempotency-Key` (kecuali sign/approve/acknowledge yang menerimanya opsional).

| Grup | Endpoint |
|---|---|
| Auth (publik) | `POST /auth/login`, `/auth/refresh`, `/auth/forgot-password`, `/auth/reset-password`; `POST /auth/logout` (login) |
| Profil | `GET /me`, `GET /me/capabilities` |
| QR tanda tangan | `GET /me/signature-qr`, `GET /me/signature-qr/raw` |
| Template | `GET /templates`, `GET /templates/{typeId}` |
| Routing | `GET /routing/organizations`, `/routing/resources?facilityId=`, `/routing/facilities`, `/routing/organizations/{id}/candidates` |
| Letters | `POST /letters/drafts`, `GET /letters/{id}`, `PUT /letters/{id}/draft`, `POST /letters/{id}/cancel`, `POST /letters/routing-preview`, `POST /letters/{id}/preview`, `GET /letters/{id}/previews/{jobId}`, `GET /letters/{id}/documents/{documentId}`, `POST /letters/{id}/submit`, `POST /letters/{id}/resubmit` |
| Tugas (query) | `GET /tasks`, `GET /tasks/{id}`, `GET /letters/{id}/workflow`, `GET /tasks/{id}/document`, `GET /tasks/{id}/delegate-candidates` |
| Tugas (aksi) | `POST /tasks/{id}/sign`, `/approve`, `/acknowledge`, `/reject`, `/request-revision`, `/defer`, `/resume`, `/delegate`, `/revoke-delegation` |
| Reservasi fasilitas | `GET /rooms`, `GET /rooms/{id}/schedule?from=&to=`, `POST /rooms/check-availability`, `POST /rooms/bookings`, `POST /rooms/bookings/{id}/confirm`, `POST /rooms/bookings/{id}/cancel` |
| Verifikasi publik | `GET /verify/{code}`, `POST /verify/upload` (multipart PDF) |
| Webhook | `POST /webhooks/resend` (tanpa login; verifikasi signature) |
| Sistem | `GET /health` (publik), `GET /openapi/v1.json` (development) |

Respons memakai problem+json dengan `code` stabil. Perubahan/penulisan memakai expected
revision/version + optimistic concurrency; stale request ditolak 409.

---

## 7. Modul & perilaku penting

### Authentication
Login email/password (akun seed), JWT RS256 + sesi server-side (security stamp dicek per request),
lockout, refresh rotasi sekali pakai, reset password sekali pakai lewat email. Kategori akun:
`StudentGeneral`, `StudentDagri`, `BAAK`, `Management`; UI surface diturunkan (Student/Management).
Capability berasal dari assignment aktif, bukan kategori.

### Letters & Routing
Draft → preview (worker render, hash input & slot) → submit/resubmit. Resolver routing menentukan
urutan chain dari jenis surat, organisasi, dan fasilitas:
Proposal/LPJ 5 tahap · Ruangan PS/SAW 6 tahap · Ruangan ber-Dagri (D3/lapangan) 7 tahap ·
Peminjaman barang 6 tahap. Revisi immutable + snapshot peserta/aturan; revisi baru mengulang TTD.

### Workflow tasks
Aksi per tahap: `Sign`, `ApproveAndSign`, `Acknowledge`, plus keputusan `reject`, `request-revision`,
`defer`, `resume`, `delegate`, `revoke-delegation`. Hanya tugas aktif dan actor berwenang
(assignment sesuai scope + DomainCode + capability; delegasi maksimal satu tingkat) yang bisa
bertindak. Idempotency replay mengembalikan hasil sama untuk key yang sama.

### Signatures
QR dibuat backend per akun (idempoten, snapshot versi/hash per evidence). Tindakan sign/approve
menulis `SignatureEvidence` + `SigningAttempt` transaksional, lalu tahap berikutnya aktif.
Finalisasi merender PDF final dengan overlay QR semua actor + kode verifikasi publik
(`GET /verify/{code}`, upload hash check).

### Rooms (reservasi fasilitas)
`POST /rooms/bookings` membuat reservasi `Pending`; konfirmasi terjadi saat aksi final workflow
(dipanggil `RoomReservationService.TryConfirmForRevisionAsync`) **atau** manual oleh pemohon/petugas
(`confirm`). Anti-bentrok dijamin database dengan exclusion constraint pada rentang
`[StartsAt, EndsAt)` untuk status `Confirmed` di resource yang sama — dua approval bersamaan:
satu menang, yang kalah menerima `409 resource_schedule_conflict`, tugas tetap aktif, dan surat
masuk `AwaitingResourceResolution` sampai konflik selesai. Waktu disimpan UTC, ditampilkan
Asia/Jakarta (+07:00). Kegiatan yang berakhir tepat saat kegiatan lain mulai tidak dianggap bentrok.

### Email
Dua jalur:
1. **Reset password** — outbox auth (`auth_password_reset_emails`) + worker khusus.
2. **Notifikasi workflow generik** — outbox `email_deliveries`:
   - Event: pengajuan terkirim, tugas aktif (tugas baru/berpindah), revisi, ditolak, selesai.
   - Reminder SLA: tugas `Active` dengan `DueAt` terlewati; cooldown default 24 jam, maksimum
     `Email__MaxRemindersPerTask`; dedup key harian Asia/Jakarta.
   - **Deduplikasi** dijaga unique `DeduplicationKey` per event/entitas/penerima.
   - **Penghentian**: reminder batal saat tugas selesai/reject/revisi/defer; worker juga
     melewati delivery reminder jika tugas sudah tidak `Active`.
   - Isi email hanya ringkas + tautan (`Email__AppBaseUrl` + `Email__LetterPathTemplate`),
     tanpa lampiran dokumen; tindakan tetap memerlukan login & otorisasi.
3. **Webhook Resend** (`POST /webhooks/resend`): verifikasi signature Svix (raw body + timestamp),
   dedup event per provider+event id, suppression otomatis untuk bounce permanent/complaint;
   alamat tersuppressed tidak dikirim lagi oleh worker.

Worker berjalan sebagai `BackgroundService` di proses API: `PasswordResetEmailWorker`,
`EmailDeliveryWorker`, `WorkflowReminderWorker`, `LetterPreviewWorker`. Di hosting, aktifkan
Always On / proses worker yang benar-benar berjalan (`Email__WorkerEnabled=true`).

### Audit
Semua mutasi penting menulis `app_audit_logs` (actor, aksi, entitas, revisi, korelasi, IP/UA).
Replay idempotency disimpan sebagai audit `Workflow`/payload ter-hash.

---

## 8. Testing

```powershell
dotnet test tests/SignIt.Auth.Tests/SignIt.Auth.Tests.csproj          # 15 unit (auth, lockout, token)
dotnet test tests/SignIt.Email.Tests/SignIt.Email.Tests.csproj        # 19 unit (webhook, outbox, reminder)
dotnet test tests/SignIt.Rooms.Tests/SignIt.Rooms.Tests.csproj        # 11 unit (interval, model reservasi)
dotnet test tests/SignIt.Signatures.Tests/SignIt.Signatures.Tests.csproj
```

Proyek `SignIt.Signatures.Tests` memuat unit + integrasi (sebagian butuh PostgreSQL/fixture);
jalankan dengan database dev aktif. Verifikasi end-to-end email/reminder pernah dilakukan live
terhadap Resend (sandbox + allowlist) dan tercatat di riwayat commit.

---

## 9. Konvensi

- Conventional Commits (`feat:`, `fix:`, `build:`, `chore:`, `docs:`).
- Jangan commit `.env`, `secrets.env`, key, atau password. Gunakan env/user-secrets/secret store.
- Migration selalu lewat `dotnet ef` dengan lokasi/nampa folder tetap (§3).
- Satu style controller (MVC controller) dengan DTO eksplisit; envelope error problem+json.
- Ikuti `AGENTS.md` untuk batas modul dan aturan otorisasi.

---

## 10. Diketahui belum selesai

- Aset template surat belum tersedia di repo (`templates/`) — katalog/preview menunggu aset seed.
- Halaman detail surat frontend (`/surat/{id}`) belum ada; tautan email memakai path yang dapat
  dikonfigurasi (`Email__LetterPathTemplate`).
- Email tugas aktif dikirim ke `AssignedUserId`; notifikasi ke penerima delegasi aktif belum
  ditambahkan (PRD §12.2).
- `GET /openapi/v1.json` di development sedang error (schema Guid) — endpoint tetap berjalan;
  daftar di §6 diambil langsung dari kode.
- Modul OCR/scan, chatbot, inventory, dan queue unit belum diimplementasikan.
- Budget kuota pada worker `EmailDeliveryWorker` belum memakai `auth_reset_email_budget`
  (hanya worker reset password yang memakainya).
