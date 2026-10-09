# Workflow — kontrak backend

## Cakupan

Chain sequential memakai snapshot submit dari Routing (5/6/7 tahap). Hanya tugas Active pada revisi
InProgress terkini dapat sign/approve/reject/request-revision/defer. Assignment actor harus aktif,
sesuai organisasi, jabatan, capability dan periode. Pengaju tidak boleh menjadi actor ApproveAndSign
untuk surat sendiri. Kategori akun/UI saja tidak memberikan hak tindakan.

Semua mutasi tugas memakai transaksi dan kunci baris LetterRequest lebih dahulu; task/letter dimuat ulang
setelah menunggu kunci. RowVersion, revision dan hash tetap dicek. Status, evidence dan receipt/audit
disimpan atomik. Key terikat actor + entity + operasi; payload berbeda dengan key yang sama menghasilkan 409.
Tidak perlu migrasi baru untuk Workflow: tabel task, delegation, revisions dan audit sudah tersedia.

## Endpoint baca

| Endpoint | Isi |
|---|---|
| GET /api/v1/tasks?page=1&pageSize=20 | Antrean Active berizin; pageSize 1–100, page 1–10000; Total dihitung setelah filter izin |
| GET /api/v1/tasks/{id} | Detail, Version, RevisionId, ContentHash, AllowedActions, DocumentUrl |
| GET /api/v1/tasks/{id}/document | PDF review private + pemeriksaan hash; owner/actor berizin saja |
| GET /api/v1/tasks/{id}/delegate-candidates | Kandidat provisioned sesuai jabatan/scope; hanya pemilik asli tugas |
| GET /api/v1/letters/{id}/workflow | Status surat, Version, chain revisi terkini dan 100 event timeline terakhir |

Respons baca no-store; PDF juga nosniff. Urutan antrean FIFO ActivatedAt lalu Id, bukan SubmittedAt.
Tidak ada estimasi waktu selesai/antrean kampus global. Owner dapat melihat tugas surat sendiri, tetapi
tidak memperoleh hak bertindak untuk peserta lain. Participant/mandat harus masih berizin untuk melihat
surat; pengguna lain memperoleh 404. Detail berisi status dan AllowedActions dari server, bukan perhitungan frontend.

DueAt tidak diisi dengan SLA kampus yang belum dikonfigurasi. Penundaan memberi batas waktu eksplisit;
IsOverdue memakai DueAt bila tersedia. Worker reminder/SLA berada dalam pekerjaan email berikutnya.

## Endpoint tindakan tugas

Semua POST di bawah wajib header `Idempotency-Key` (1–80 karakter, tanpa control character).
Frontend harus mengambil versi dari detail tugas tepat sebelum konfirmasi, lalu refresh sesudah 409.

- POST /api/v1/tasks/{id}/sign: hanya ActionType Sign.
- POST /api/v1/tasks/{id}/approve: hanya ActionType ApproveAndSign, sekaligus QR actor.
- POST /api/v1/tasks/{id}/acknowledge: tidak boleh menggantikan peserta TTD Required. Tidak membuat evidence QR.

Payload sign/approve/acknowledge:

```json
{
  "expectedRevisionId": "UUID revisi",
  "expectedContentHash": "SHA-256 revisi",
  "expectedTaskVersion": "UUID Version dari detail tugas",
  "comment": "Opsional, maksimal 1000 karakter"
}
```

Payload keputusan lain:

```json
{
  "expectedRevisionId": "UUID revisi",
  "expectedContentHash": "SHA-256 revisi",
  "expectedTaskVersion": "UUID Version dari detail tugas",
  "reason": "Alasan wajib",
  "delegateUserId": null,
  "until": null
}
```

| POST suffix | Aturan |
|---|---|
| reject | Reason ≤1000; surat Rejected; tugas lain yang belum selesai Cancelled |
| request-revision | Reason ≤1000; surat NeedsRevision; tugas lain yang belum selesai Cancelled |
| defer | Reason ≤1000 + Until UTC di masa depan, maksimal 30 hari; tetap tahap wajib |
| resume | Hanya Deferred; kembali Active pada tugas yang sama, tidak melompat tahap |
| delegate | Reason ≤500 + DelegateUserId + Until; masa mandat maksimal 30 hari |
| revoke-delegation | Pemilik asli dapat mencabut mandat saat Active/Deferred; versi tugas berubah |

Keputusan mengembalikan 200 dengan TaskId, Status, TaskVersion, LetterStatus, LetterVersion,
DelegationId (jika baru diberikan). Perubahan kontrak: reject/request-revision yang sebelumnya
hanya Reason dan 204 sekarang memerlukan expected revision/hash/task version dan key, serta mengembalikan DTO.
Sign/approve juga kini mewajibkan ExpectedTaskVersion dan key; payload lama tanpa versi tidak berlaku.

Deferred tidak masuk antrean Active dan tidak dianggap selesai. Tidak ada auto-skip/auto-approve setelah Until.
Resume eksplisit mempertahankan batas waktu penundaan agar overdue tidak hilang diam-diam. Mandat terbatas
`task:{UUID tanpa tanda hubung}`, satu tingkat, hanya dapat dibuat pemilik asli. Pengganti harus eligible
pada saat mulai dan sampai akhir mandat; mandat broad/null dari data lama tidak lagi memberikan hak umum.
Mandat baru mengganti mandat aktif tugas tersebut. Assignment pemberi/penerima tetap dicek saat digunakan.
SignatureEvidence mengambil QR actor aktual dan mencatat DelegatedFromUserId serta keterangan `a.n.`.

## Revisi, edit dan pembatalan

PUT /api/v1/letters/{id}/draft + key:

```json
{
  "expectedVersion": "UUID versi surat",
  "expectedRevisionId": "UUID revisi",
  "expectedContentHash": "SHA-256 revisi",
  "title": "Judul baru",
  "fields": { "nama_kegiatan": "Nilai baru dan seluruh field user lainnya" }
}
```

PUT adalah penggantian lengkap field user (bukan patch). Hanya Draft/NeedsRevision, hanya owner, tipe
surat tetap. Setiap edit membuat revisi immutable baru, bukan mengubah revisi lama. NeedsRevision tetap
berstatus demikian sampai resubmit, tetapi current revision tanpa tasks dapat dibuatkan preview baru.
Preview revisi submitted lama tidak dapat dipakai ulang. Evidence/task selesai historis tidak dihapus.

Generate preview → polling Ready → POST /api/v1/letters/{id}/resubmit memakai payload SubmitLetterRequest
yang sama dengan submit. Kedua route memvalidasi kondisi yang sama. Resubmit mengulang routing terkini,
freezes snapshot baru, membuat seluruh peserta/tasks baru dari tahap 1; evidence revisi lama bukan approval
isi baru. Nomor referensi SGN tetap, sedangkan RevisionNo bertambah (termasuk revisi edit/submit immutable).
Nomor ini masih referensi aplikasi, bukan nomor resmi unit kampus.

POST /api/v1/letters/{id}/cancel + key menerima ExpectedVersion, ExpectedRevisionId,
ExpectedContentHash dan Reason wajib ≤1000. Hanya owner pada Draft/InProgress/NeedsRevision.
Pending/Active/Deferred menjadi Cancelled; mandat tugas dicabut; evidence historis dipertahankan.
Completed tidak dapat dibatalkan lewat endpoint ini (pencabutan/revoke adalah fitur terpisah).

Kode konflik penting: stale_task, revision_hash_mismatch, inactive_letter_revision, task_not_active,
previous_task_incomplete, stale_letter, stale_draft, stale_content, stale_preview, idempotency_payload_conflict.
Authorization tetap dilakukan sebelum mengembalikan receipt retry; key tidak memberikan hak tambahan.

## Verifikasi dan batas

Tes PostgreSQL memakai SIGNIT_TEST_CONNECTION dan database terpisah berakhiran `_tests`.
WorkflowPostgresTests mencakup chain 5/6/7, salah giliran, versi/hash/reason, ulang payload/key,
dua sign bersamaan, sign melawan reject, revisi ulang semua tahap, bukti historis, nomor tetap,
cancel, defer/resume, mandat lintas tugas/expired/salah scope, actor QR, inbox dan download private.
Adapter PDF diganti khusus pada tes final-state untuk menguji transaksi/state machine, bukan visual QR.
Ada pengujian failure adapter yang memastikan semua evidence tetap tersimpan dan surat ProcessingFailed,
bukan Completed; retry key tidak meminta penandatangan mengulang tindakan.

`scripts/test-workflow.ps1` memakai API lokal dengan database uji dan email disabled. Pengujian HTTP mencakup
login demo beberapa actor, antrean, download PDF 200, sign/replay, request-revision, edit, renderer preview,
resubmit, defer/resume dan cancel; sesi selalu di-logout. Tidak mengirim email produksi.

Belum selesai di pekerjaan ini:

- #3: adapter PDF produksi menolak PNG QR (`Unsupported image format`); finalization masih inline,
  belum job/lease/retry worker lengkap; label DRAFT, nomor resmi dan tampilan mandat PDF belum dituntaskan.
  Evidence terakhir sudah disimpan dalam transaksi sebelum finalizer membaca database.
- #4: pemeriksaan/reservasi jadwal fasilitas yang dijamin database pada persetujuan terakhir belum ada.
- #5: email outbox Workflow dan reminder SLA belum terhubung (worker reset-password tidak menggantikannya).
- #6: frontend belum mengonsumsi endpoint/kontrak tindakan di atas.

Jangan menyebut backend release-ready atau PDF final valid hanya karena tes state machine lulus.
