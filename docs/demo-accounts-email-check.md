# Akun demo lokal dan pemeriksaan email

Provisioning: jalankan `dotnet run --no-launch-profile -- --provision-demo` dari backend dengan `ASPNETCORE_ENVIRONMENT=Development`, setelah migrasi dan seed organisasi tersedia.

Kredensial acak per akun disimpan di `.data/demo/credentials.json` (diabaikan Git). Jangan dibagikan atau dipakai di production. File berisi password plaintext untuk pengujian lokal; database hanya menyimpan hash. Provisioning ulang tidak menggandakan akun atau mereset password. Jika password telah diubah, file ini hanya mencatat password awal.

Tersedia 12 pengaju (satu per organisasi), serta 9 akun peran: ketupel, ketua, pembina, kemahasiswaan, minat-bakat, dagri, baak, wadir3, wadir2. Alamatnya `<peran>@demo.signit.example`; pengaju memakai `pengaju-<scope>@demo.signit.example`. Total 21 akun, 108 assignment dan 21 QR. Assignment Kemahasiswaan hanya untuk Himpunan, Minat Bakat hanya untuk Organisasi. Akun peran lintas organisasi adalah fixture demo, bukan mandat nyata. Email `.example` tidak dapat menerima reset password.

## Hasil pemeriksaan kode email yang ter-pull

- Ada worker reset password, adapter Resend, webhook dengan signature/replay timestamp, deduplikasi event, serta suppression untuk bounce/complaint.
- Tidak ditemukan worker/jadwal reminder workflow atau pengiriman email untuk tugas surat. Hosted worker yang terdaftar adalah PasswordResetEmailWorker.
- Migrasi AddEmailWebhookEvents sudah diterapkan ke database lokal. Pengiriman eksternal tetap tidak diaktifkan.
- EfEmailEventStore menangkap semua unique violation sebagai event duplikat. Dua event berbeda untuk alamat suppression yang sama dapat mengalami konflik insert bersamaan; konflik tersebut bisa dianggap duplikat padahal event baru belum tersimpan. Perlu upsert suppression atau retry terarah dan pemeriksaan constraint duplikat event.
- ResendWebhookVerifier belum menangani timestamp numerik di luar rentang DateTimeOffset: FromUnixTimeSeconds dapat melempar exception sehingga request invalid menjadi HTTP 500.

Reminder surat masih membutuhkan outbox atomik dengan transisi workflow, penerima dari tugas aktif, jadwal SLA, deduplikasi per tugas/revisi/periode, dan penghentian saat tugas selesai, direvisi, dibatalkan, atau assignment berubah. Pemeriksaan ini tidak mengubah service email rekan tim.
