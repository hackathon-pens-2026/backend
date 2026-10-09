# Routing peminjaman fasilitas

Keputusan pengguna terbaru: Pasca dan SAW tidak memerlukan tanda tangan Dagri BEM.
Chain: Ketua Pelaksana → Ketua Organisasi → Pembina → Kemahasiswaan → BAAK → Wakil Direktur III.
Ini menggantikan chain tujuh tahap Pasca/SAW dalam PRD v2.6.

Routing preview menerima facilityCode PASCA atau SAW untuk peminjaman-ruangan.
Fasilitas kosong atau kebijakan fasilitas yang belum tersedia menghasilkan error;
chain Pasca/SAW tidak otomatis diterapkan untuk D3, D4 atau lapangan.

Saat katalog resource database diimplementasikan, fasilitas harus diambil backend dari ResourceId
yang dipilih dan divalidasi ulang saat submit. FacilityCode pada preview belum merupakan bukti reservasi.
Task historis tidak diubah oleh perubahan kebijakan ini.

