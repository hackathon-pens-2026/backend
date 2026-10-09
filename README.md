# SignIt Backend — Authentication

Implementasi auth akun internal mengikuti `../SignIt-PRD.md` v2.6. Tidak ada registrasi, SSO, role picker, admin, atau endpoint CRUD akun/configuration.

## Struktur

- `backend.csproj` dan `Program.cs`: satu proyek/startup API (`SignIt.Api`).
- `Modules/Authentication/Controllers`: endpoint autentikasi dan profil.
- `Modules/Authentication/DTOs`: request/response DTO dan error use case.
- `Modules/Authentication/Models`: akun, assignment, sesi, refresh/reset token, audit, outbox dan budget email.
- `Modules/Authentication/Services`: use case, interface dependency, password hashing/policy dan JWT RS256.
- `Modules/Authentication/Data`: store PostgreSQL, provisioning dan metadata OpenAPI auth.
- `Infrastructure/Persistence/AppDbContext.cs` dan `Migrations/`: context bersama dan migration.
- `Infrastructure/Email`: adapter Resend, proteksi payload dan worker email reset.
- `Infrastructure/Errors`: pemetaan error HTTP bersama.
- `Modules/Letters`, `Templates`, `Workflow`, `Signatures`, `Rooms`, `Inventory`, `Chat`, serta `Infrastructure/Storage` dan `Llm`: direktori disiapkan, belum ada implementasi fitur.
- `Modules/Email`: model event provider, suppression alamat, verifikasi signature Svix dan endpoint webhook Resend.
- `tests/SignIt.Auth.Tests`: pengujian domain yang disiapkan, **belum dijalankan**.
- `Dockerfile` dan `.dockerignore`: image multi-stage .NET 10 (runtime `10.0`), entrypoint `SignIt.Api.dll`.

Konsolidasi cabang: pekerjaan auth (dua stash lama) digabung dengan hasil `pull origin main` (dukungan Docker + paket lebih baru dari tim), lalu dirapikan menjadi satu backend. `Data/AppDbContext.cs` (stub duplikat dari main) dihapus karena context tunggal ada di `Infrastructure/Persistence/AppDbContext.cs`. Stash lama **tidak dihapus** agar riwayat tetap aman.

Struktur modular satu proyek ini mengikuti permintaan terbaru, menggantikan pemisahan proyek `SignIt.Domain`/`SignIt.Application`/`SignIt.Infrastructure`. Batas tanggung jawab tetap dijaga lewat folder/namespace: `Modules/Authentication/Models` (domain), `Services`/`DTOs` (application), serta `Data` dan `Infrastructure` (EF Core, provider, worker). Models tidak bergantung pada ASP.NET Core/EF Core; controller tetap tipis. Ini deviasi sadar dari tata letak empat proyek pada `AGENTS.md`/PRD dan dicatat agar boundary tidak tercampur. Tidak ada perubahan kontrak endpoint atau skema tabel akibat pemindahan.

## Endpoint `/api/v1`

| Method | Path | Input / hasil |
|---|---|---|
| POST | `/auth/login` | `{ "email", "password" }` → access/refresh token dan profil |
| POST | `/auth/refresh` | `{ "refreshToken" }` → kedua token baru |
| POST | `/auth/logout` | Bearer token → 204, mencabut sesi saat ini |
| GET | `/me` | Bearer token → profil, kategori, UI surface, capabilities dan assignment aktif |
| GET | `/me/capabilities` | Bearer token → kategori/surface/capabilities/assignment |
| POST | `/auth/forgot-password` | `{ "email" }` → 202 dengan pesan netral |
| POST | `/auth/reset-password` | `{ "token", "newPassword" }` → mengganti password dan mencabut seluruh sesi |
| POST | `/webhooks/resend` | Webhook provider: verifikasi signature raw body, dedup event, suppression bounce/complaint |

Respons menggunakan camelCase dan enum string. Contoh login/refresh:

```json
{
  "accessToken": "<JWT>",
  "accessTokenExpiresAt": "2026-10-09T10:10:00Z",
  "refreshToken": "<opaque-token>",
  "refreshTokenExpiresAt": "2026-10-16T10:00:00Z",
  "tokenType": "Bearer",
  "user": {
    "id": "10000000-0000-0000-0000-000000000001",
    "name": "Mahasiswa Demo",
    "email": "student@signit.example",
    "nimNip": null,
    "isActive": true,
    "emailVerifiedAt": null,
    "userCategory": "StudentGeneral",
    "uiSurface": "Student",
    "capabilities": ["Requester"],
    "assignments": []
  }
}
```

Kategori `StudentGeneral`/`StudentDagri` memakai `Student`; `BAAK`/`Management` memakai `Management`. `Requester` adalah capability dasar akun aktif. Capability lain hanya berasal dari assignment aktif dengan masa berlaku `[ValidFrom, ValidTo)`, bukan kategori. Metadata ini membantu navigasi, **bukan izin otomatis untuk bertindak pada semua task/unit**. Modul workflow nanti wajib memeriksa task, scope, ownership dan delegasi sendiri.

Error berupa `application/problem+json` dengan `code`, `traceId`, dan `errors` untuk validasi. Status: 400 invalid input/reset, 401 kredensial/sesi tidak valid, 403 forbidden, 409 konflik state, 429 rate limit, 503 kegagalan persistence. Tidak mengembalikan exception/provider payload internal.

OpenAPI development: `/openapi/v1.json`. Contoh request manual ada di `backend.http`; jangan menyimpan password/token nyata di file itu.

## Konfigurasi sebelum menjalankan

Restore/build tidak membutuhkan database atau secret. API/provisioning membutuhkan konfigurasi nyata; nilai kosong sengaja **bukan bypass autentikasi**.

| Nama environment variable | Kebutuhan |
|---|---|
| `ConnectionStrings__DefaultConnection` | Connection string PostgreSQL (dev `main` memakai port 5434); simpan sebagai secret |
| `Jwt__PrivateKeyPem` | Private key RSA PEM minimal 2048-bit; tidak digenerate ulang tiap restart |
| `Jwt__Issuer`, `Jwt__Audience`, `Jwt__KeyId` | Harus konsisten pada seluruh replica/API |
| `Cors__AllowedOrigins__0` | Exact frontend origin, tanpa wildcard; kosong untuk BFF server-to-server |
| `Proxy__KnownProxies__0` | IP reverse proxy tepercaya bila memakai forwarded headers |
| `DataProtection__KeyDirectory` | Direktori key persisten dan bersama antar-replica/worker |
| `Email__WorkerEnabled` | Default `false`; aktifkan eksplisit setelah PostgreSQL/Resend tersedia |
| `Email__ResetPasswordUrl` | URL frontend HTTPS tanpa query/fragment; bukan input dari client |
| `Email__From`, `Email__ReplyTo`, `Resend__ApiKey` | Pengirim terverifikasi, mailbox bantuan, secret API key |
| `Email__SandboxMode`, `Email__RecipientAllowlist__0` | Sandbox default aktif; non-production wajib sandbox |
| `Email__DailyBudget`, `Email__MonthlyBudget` | Alokasi budget pengiriman, sesuaikan kuota akun dan pengirim lain |
| `Resend__WebhookSecret` | Secret `whsec_...` untuk verifikasi signature webhook; kosong membuat endpoint mengembalikan 503 |
| `Resend__WebhookToleranceSeconds` | Toleransi timestamp signature (default 300 detik) |

User-secrets development juga didukung. Contoh aman (ganti placeholder **secara lokal**, jangan commit secret):

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5434;Database=hackathondb;Username=<user>;Password=<secret>" --project backend.csproj
dotnet user-secrets set "Jwt:PrivateKeyPem" (Get-Content -Raw ".data/auth-signing.pem") --project backend.csproj
```

`.env` lokal juga didukung: salin `.env.example` menjadi `.env` (ter-ignore git) dan isi nilainya. Loader `Infrastructure/Configuration/DotEnv.cs` memuat `.env` untuk `dotnet run` maupun `dotnet ef`, tidak pernah menimpa environment variable yang sudah ada (konfigurasi deployment selalu menang), dan file `.env` tidak boleh di-commit. Nilai quoted boleh multi-baris, sehingga `Jwt__PrivateKeyPem` bisa ditulis sebagai PEM utuh.

Private key RSA harus dibuat/disediakan tim melalui alat pengelolaan key yang sesuai. `.data/` di-ignore dan tidak dipublish. Saat production, simpan JWT key di secret store serta lindungi key ring Data Protection dengan akses terbatas, storage persisten terenkripsi dan backup; filesystem ephemeral tidak cukup. Implementasi ini memakai filesystem key ring, belum adapter Azure Key Vault/Blob untuk key ring.

Forwarded headers hanya dipakai bila IP proxy tepercaya dikonfigurasi; jangan mengaktifkan trust semua proxy atau menerima IP arbitrer dari client. Rate limiter per-IP bersifat lokal per proses; account lockout, reset cooldown, sesi dan budget email dipersistensikan bersama di PostgreSQL. Tambahkan proteksi edge/distributed rate limit sebelum deployment multi-instance berskala besar.

## Migrasi dan provisioning — jalankan nanti setelah database tersedia

Migration `InitialAuthentication` dan `provisioning/auth-schema.sql` sudah dibuat **tanpa koneksi database**. SQL hanya menambah tabel/index/constraint auth; tidak mereset database atau mengisi password. Review konflik nama tabel dan privilege sebelum diterapkan. Jangan menjalankan SQL dan migration dua kali pada database yang sama.

Perintah berikut **belum dijalankan**:

```powershell
dotnet ef database update --project backend.csproj --context AppDbContext
```

Untuk design-time/database update, EF factory membaca `ConnectionStrings__DefaultConnection` dari environment, bukan user-secrets. Placeholder tanpa password pada factory hanya untuk scaffolding SQL; tidak boleh dianggap konfigurasi database nyata.

Salin `provisioning/auth.example.json` menjadi `provisioning.local.json`, perbaiki nama/email/scope/jabatan/masa berlaku sesuai data tim, dan isi empat environment variable password yang disebut manifest. Contoh kategori/assignment bukan kebijakan resmi kampus. `emailVerified: true` hanya boleh digunakan setelah tim memverifikasi alamat; tidak ada verifikasi email publik/aktivasi akun dalam MVP.

```powershell
dotnet run --project backend.csproj --no-launch-profile -- --provision-auth provisioning.local.json
```

Provisioning hanya melalui CLI, tidak berjalan otomatis saat startup, dan tidak mengirim email. ID/email stabil mencegah akun ganda; akun/assignment lama tidak ditulis ulang dan password lama tidak direset. Manifest yang bertentangan ditolak; koreksi memerlukan script ter-audit. Aset QR adalah tanggung jawab modul Signatures berikutnya, bukan token auth dan belum dirender oleh fitur ini.

Setelah siap, jalankan API memakai profil HTTPS yang sudah ada:

```powershell
dotnet run --project backend.csproj --launch-profile https
```

Batasi privilege runtime PostgreSQL: audit memerlukan INSERT/SELECT, bukan UPDATE/DELETE. Gunakan credential terpisah untuk migration/operasional. Retensi audit/token/outbox serta script cleanup harus ditentukan tim; consumed refresh token jangan dihapus sebelum sesi berakhir karena dibutuhkan untuk deteksi replay.

## Keamanan sesi dan integrasi frontend

- Password memakai ASP.NET Core Identity V3 PBKDF2-SHA512 dengan salt dan default 210.000 iterasi; hash lama dapat di-upgrade setelah login berhasil. Minimum password/passphrase 12 karakter, maksimum 128; tidak memotong password.
- Default JWT 10 menit, sesi absolut 7 hari. Signature RS256, issuer, audience, expiry dan algoritma divalidasi. Setiap request berizin juga memeriksa sesi/security stamp di database, sehingga logout/reset efektif sebelum JWT kedaluwarsa.
- Refresh/reset token acak 256-bit; database menyimpan SHA-256, bukan token mentah. Refresh dirotasi sekali pakai. Pemakaian ulang refresh lama mencabut seluruh sesi/family terkait. Reset token sekali pakai, default 30 menit; permintaan baru membatalkan reset sebelumnya.
- Lima login gagal menyebabkan lockout 15 menit. Akun nonaktif, lockout dan password salah mendapat pesan login netral yang sama. Forgot-password netral juga untuk akun tidak ada/nonaktif/cooldown.
- Gunakan Next.js BFF untuk Vercel–Azure: simpan access/refresh di server BFF dan berikan cookie sesi `HttpOnly; Secure; SameSite` ke browser. **Jangan menyimpan refresh token di localStorage/sessionStorage.** API ini tidak memakai cookie browser; CSRF/origin checks harus diterapkan pada endpoint cookie milik BFF.
- BFF wajib menserialkan refresh per sesi (single-flight). Dua refresh bersamaan/retry token lama dianggap replay. Jika respons refresh hilang dan token baru tidak diketahui, minta login ulang; jangan retry buta dengan token lama.
- Respons auth/profile tidak boleh di-cache publik. Jangan memasukkan token/password ke log, query string atau telemetry.

## Email reset password

Forgot-password menyimpan token dan outbox secara atomik; API tidak menunggu Resend. Dengan worker default nonaktif, **email hanya queued dan belum dikirim**, bukan simulasi pengiriman sukses. Worker aktif membutuhkan persistent hosting/Always On atau proses worker yang benar-benar berjalan.

Worker memakai PostgreSQL row lock/lease, stable Resend idempotency key, retry bounded/backoff/Retry-After, sandbox allowlist dan budget harian/bulanan UTC yang dicadangkan sebelum setiap HTTP attempt. Budget ini konservatif termasuk retry; kuota provider tetap authoritative. Token expired/sudah dipakai/dibatalkan tidak dikirim. Kuota yang tertunda sampai token expired memerlukan permintaan reset baru.

Payload token pada outbox dienkripsi menggunakan Data Protection dan dibersihkan setelah outcome terminal. `Accepted` hanya berarti Resend menerima pesan, **bukan Delivered/dibaca**. Timeout ambigu disimpan sebagai `Unknown`; setelah batas retry/jendela aman, retry dihentikan untuk rekonsiliasi tim, bukan dikirim ulang buta. Webhook delivered/bounce/suppression umum adalah cakupan modul Email berikutnya dan belum diimplementasikan di fitur auth ini.

Tautan email memakai `#token=...`, sehingga token tidak masuk query/log server/referrer. Halaman frontend harus membaca fragment, segera membersihkan URL, dan hanya mengirim token lewat body POST setelah user memilih password baru. Kunjungan GET/email scanner tidak mengubah password. Matikan open/click tracking di pengaturan Resend untuk email transaksi.

## Webhook status pengiriman (Resend)

- `POST /api/v1/webhooks/resend` menerima event provider tanpa login pengguna. Signature Svix diverifikasi dari raw body + timestamp (toleransi default 300 detik); permintaan tanpa signature sah ditolak 401 dan secret yang belum dikonfigurasi menghasilkan 503.
- Event disimpan per provider + event ID (`email_provider_events`) dengan unique constraint sehingga replay tidak menggandakan efek; event duplikat dijawab 200 `duplicate`.
- `email.bounced` (kecuali transient) dan `email.complained` menambahkan alamat ke `email_suppressions`; event dan suppression ditulis dalam satu transaksi agar retry tidak kehilangan suppression.
- Worker reset email berhenti mengirim ke alamat tersuppressed (`recipient_suppressed`, tanpa retry).
- `Accepted`/`Delivered` tetap bukan bukti email dibaca; status pengiriman terpisah dari status workflow surat.

## Verifikasi yang telah dilakukan

- `dotnet restore backend.csproj` dan restore proyek test: berhasil.
- `dotnet tool restore`: berhasil, EF CLI 10.0.12.
- `dotnet build backend.csproj --no-restore`: berhasil, 0 warning/error.
- `dotnet build tests/SignIt.Auth.Tests/SignIt.Auth.Tests.csproj --no-restore`: kompilasi berhasil, **bukan eksekusi tes**.
- Paket versi terbaru tanpa downgrade/bentrok: `Microsoft.AspNetCore.OpenApi` & `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.12, `Microsoft.EntityFrameworkCore`/`Relational`/`Design` 10.0.12, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `System.IdentityModel.Tokens.Jwt` 8.23.0, `Microsoft.OpenApi` 2.12.2.
- `Microsoft.EntityFrameworkCore.Relational` dipin eksplisit agar proyek tes tidak turun ke 10.0.4 lewat dependensi minimum Npgsql.
- `dotnet ef migrations has-pending-model-changes`: tidak ada perubahan model; migration `InitialAuthentication` diregenerasi dengan EF 10.0.12 (ProductVersion 10.0.12) dan `provisioning/auth-schema.sql` diperbarui. Belum diterapkan ke database.
- `Microsoft.OpenApi` 2.12.2 memuat perbaikan advisory `GHSA-v5pm-xwqc-g5wc`.

Sesuai permintaan, **tidak menjalankan `dotnet test`, API, provisioning, migration update, request endpoint, atau pengiriman email**. Database belum tersedia. Login lintas akun, concurrency refresh/reset/lockout, migration PostgreSQL, pemulihan key ring, Resend dan alur BFF/CORS/CSRF tetap perlu diuji setelah layanan tersedia; build saja bukan bukti siap rilis.

### Email webhook (lanjutan)

- Unit tests `tests/SignIt.Email.Tests`: 13/13 lulus (signature Svix valid/tampered/expired/multi-signature, dedup event, suppression bounce permanent/complaint, bounce transient tidak men-suppress, payload invalid).
- Verifikasi live terhadap API: event valid → 200 `processed`; event ID sama → 200 `duplicate`; signature salah → 401; `email.bounced` permanent dan `email.complained` membuat baris `email_suppressions`; bounce transient tidak. Data uji dibersihkan dari database development.
- Migration `AddEmailWebhookEvents` diterapkan ke PostgreSQL development; `has-pending-model-changes` bersih.
