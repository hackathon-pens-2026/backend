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

Renderer harus menyediakan Document berjenis Review, RevisionId milik draft, ProcessingState Ready,
MimeType application/pdf, hash SHA-256 dan file private. Tidak ada endpoint untuk client mendaftarkan
StorageKey arbitrer. Integrasi renderer belum tersedia sehingga preview tidak dipalsukan.
UI meninjau routing dan slot sebelum submit. Submit memvalidasi ulang assignment dan fasilitas.

Submit membekukan field, template, routing, resource dan slot dalam revisi baru; membuat satu task per tahap;
aktif hanya tahap pertama. Pemilik draft diperiksa, baris pengajuan dikunci, transaksi serializable.
Retry dengan key dan payload sama mengembalikan pengajuan yang sama; payload berbeda ditolak.
Nomor SGN-{UUID} adalah referensi aplikasi sementara, bukan format nomor surat resmi kampus.

Belum termasuk reservasi jadwal, email outbox (pekerjaan tim email), renderer, serta kebijakan nomor resmi.
Assignment akun aktual masih harus diprovisioning. Build bukan bukti integrasi PostgreSQL lintas akun.
