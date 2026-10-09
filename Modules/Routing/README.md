# Konfigurasi routing organisasi

Jenis organisasi ditentukan dari Routing:OrganizationKinds pada konfigurasi backend.
Key harus sama persis dengan Scope assignment akun. Nilai yang didukung: Himpunan atau Organisasi.
Contoh konfigurasi (scope contoh perlu diganti dengan scope yang benar-benar diprovisioning):

```json
{
  "Routing": {
    "OrganizationKinds": {
      "himpunan-contoh": "Himpunan",
      "organisasi-contoh": "Organisasi"
    }
  }
}
```

Himpunan membutuhkan assignment Approver dengan PositionCode Kemahasiswaan.
Organisasi membutuhkan assignment Approver dengan PositionCode MinatBakat
dan label Tim Pembina Minat dan Bakat. Assignment harus aktif, dalam scope yang sama,
dan berada dalam masa berlaku. Kandidat kosong/ambigu memblokir routing.
Jenis organisasi tidak diterima dari request client dan tidak ditebak dari nama scope.
Aturan ini berlaku untuk keempat tipe surat, mengganti satu tahap tanpa menambah jumlah tahap.

Pasca Sarjana (PS/PASCA) dan SAW tidak memerlukan Dagri BEM.
D3, D4, Lapangan Merah, Lapangan Futsal, Lapangan Basket memerlukan Dagri BEM.
Saat katalog organisasi/database tersedia, mapping jenis organisasi ini perlu diambil dari record organisasi.
