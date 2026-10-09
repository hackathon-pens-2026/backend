# Signature dan PDF final

Persetujuan terakhir menyimpan task, evidence dan receipt idempotency dalam satu transaksi,
kemudian menetapkan `Finalizing`. Respons tindakan tetap `IsWorkflowCompleted=false` dan
`VerificationCode=null`: approval selesai bukan berarti PDF sudah siap. Frontend harus polling
status finalisasi, bukan mengulang tanda tangan untuk mendapatkan PDF.

## Endpoint pemilik surat

- `GET /api/v1/letters/{id}/finalization`: LetterId, RevisionId, Version, Status,
  VerificationCode dan DownloadUrl (hanya setelah Completed).
- `POST /api/v1/letters/{id}/finalization/retry`: ExpectedRevisionId, ExpectedContentHash,
  ExpectedVersion dan header Idempotency-Key. Hanya ProcessingFailed, tugas harus lengkap.
  Mengantrekan ulang dengan respons 202, receipt replay tidak menggandakan antrean.
- `GET /api/v1/letters/{id}/finalization/document`: private attachment PDF; memeriksa ukuran
  dan SHA-256 sebelum download, no-store/nosniff. Bukan public download melalui kode verifikasi.

## Pemrosesan dan retry

SignatureFinalizationWorker berjalan setiap 2 detik. Status surat di PostgreSQL adalah work item
durable: restart aplikasi tetap menemukan surat Finalizing. Finalizer mengunci row surat dan
memeriksa ulang status/revisi setelah menunggu lock; dua instance tidak memublikasikan PDF ganda.
Render dan penyimpanan pada worker masih dilakukan selama lock transaksi finalisasi dipegang,
bukan di transaksi approval. Ini memprioritaskan konsistensi; untuk volume besar perlu dedicated
job/lease dan rendering di luar lock. Tidak ada tabel job/migrasi baru dalam implementasi ini.

ProcessingFailed dicoba otomatis maksimal 3 kegagalan per revisi dengan jeda minimal 30 detik.
Setelah batas tercapai, pemilik dapat meminta retry satu percobaan melalui endpoint di atas,
misalnya setelah aset dipulihkan. Tidak membuat evidence/task/signing attempt baru. AwaitingResourceResolution
tidak boleh diproses sebagai retry finalisasi: approver harus tetap menyelesaikan tugas terakhir
setelah konflik reservasi ditangani. Cancellation membatalkan transaksi, bukan menjadi surat Completed.

Semua tugas harus selesai. Review wajib cocok dengan ukuran/hash tersimpan. Evidence wajib cocok
dengan hash isi dan actor tugas; aset QR wajib cocok dengan owner, versi dan hash evidence.
PNG grayscale 1-bit QRCoder diperluas secara lossless menjadi BMP hanya untuk embedding PDFsharp;
aset original dan hash evidence tidak diubah. Halaman/koordinat invalid dan QR kosong menggagalkan render.

PDF final memakai InputJson job preview yang dibekukan, tanpa membaca ulang template terbaru.
Nomor DRAFT diganti Number surat dan footer memakai label final. Slot final harus identik dengan
slot preview/participant yang disetujui; perubahan pagination menyebabkan ProcessingFailed,
bukan overlay di lokasi salah. Review dan DataJson revisi tidak diubah. Penyimpanan final memakai
key unik per percobaan; metadata dokumen, verification record dan Completed dipublikasikan atomik.

Number saat ini `SGN-{id}` adalah referensi aplikasi, **bukan nomor resmi unit kampus**.
Kebijakan penerbitan nomor resmi perlu dikonfirmasi sebelum mengklaim penomoran administratif final.
QR actor adalah identitas evidence, bukan sertifikat tanda tangan kriptografis PDF/PAdES.
Footer menampilkan kode verifikasi, bukan domain kampus rekaan.

## Verifikasi

Suite signature/workflow mencakup PNG dan BMP nyata, evidence terakhir, review korup dan pemulihan,
retry bersamaan dengan satu publikasi final, retry endpoint idempoten, private download dan hash korup.
Empat template diuji final rendering dengan slot tidak berpindah; file QA lokal ada di `.data/renderer-qa`.
Pengujian PostgreSQL wajib memakai database terpisah berakhiran `_tests`.
