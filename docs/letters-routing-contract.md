# Letters dan routing

Routing preview POST /api/v1/letters/routing-preview memakai TypeId, OrganizationId,
CommitteeChairId, OrganizationChairId, dan ResourceId (wajib untuk ruangan/lapangan).
Scope dan FacilityCode tidak lagi diterima; backend membacanya dari database.

GET /api/v1/routing/organizations menampilkan organisasi dengan assignment Requester aktif.
GET /api/v1/routing/organizations/{id}/candidates menampilkan kandidat Ketua Pelaksana/Ketua Organisasi.
GET /api/v1/routing/facilities dan /resources?facilityId=... menampilkan katalog peminjaman.

POST /api/v1/letters/{id}/submit memerlukan header Idempotency-Key (maksimal 80 karakter)
dan ExpectedVersion, ExpectedRevisionId, ExpectedContentHash, OrganizationId, CommitteeChairId,
OrganizationChairId, ResourceId, ReviewDocumentId, ExpectedReviewHash, Slots.
Setiap slot: PositionCode, PageIndex (0-based), X, Y, Width, Height, dalam PDF point dari kiri atas.
Slot minimal 100 × 100 point; rotasi halaman harus dinormalisasi; slot tidak boleh bertumpuk.

POST /api/v1/letters/{id}/preview menerima ExpectedVersion, ExpectedRevisionId, ExpectedContentHash,
OrganizationId, CommitteeChairId, OrganizationChairId, dan ResourceId. Mengembalikan 202 + Location
untuk job baru/berjalan; 200 bila hasil identik sudah Ready. Generate tidak submit atau menandatangani.
GET /api/v1/letters/{id}/previews/{jobId} untuk polling. Respons berisi State (Pending/Processing/Ready/
Failed/Superseded), ErrorCode, ReviewDocumentId, ReviewHash, Slots, dan DownloadUrl.
GET /api/v1/letters/{id}/documents/{documentId} mengirim PDF private hanya kepada pemilik surat.
Semua respons preview/download no-store; tidak ada URL storage publik atau endpoint StorageKey arbitrer.

Worker renderer menyediakan Document berjenis Review, RevisionId milik draft, ProcessingState Ready,
MimeType application/pdf, hash SHA-256 dan file private. Gunakan ReviewDocumentId, ReviewHash dan Slots
dari respons Ready untuk submit. Client tidak boleh menentukan ulang koordinat slot. Submit memvalidasi
ulang assignment, fasilitas, fingerprint peserta/data/layout/aset template, hash PDF dan slot server.

Submit membekukan field, template, routing, resource dan slot dalam revisi baru; membuat satu task per tahap;
aktif hanya tahap pertama. Pemilik draft diperiksa, baris pengajuan dikunci; transaksi memakai
Read Committed + kunci pengajuan yang sama untuk seluruh mutasi Workflow.
Retry dengan key dan payload sama mengembalikan pengajuan yang sama; payload berbeda ditolak.
Nomor SGN-{UUID} adalah referensi aplikasi sementara, bukan format nomor surat resmi kampus.

Belum termasuk reservasi jadwal, email outbox (pekerjaan tim email), serta kebijakan nomor resmi.
Akun demo lokal tersedia; assignment akun kampus aktual masih harus diprovisioning.
Lihat template-renderer.md untuk batas renderer dan pengujian PostgreSQL.

Workflow, revisi/resubmit, penundaan, delegasi, antrean dan akses PDF penandatangan tersedia.
Lihat workflow-contract.md untuk payload versi tugas dan perubahan respons reject/request-revision.
