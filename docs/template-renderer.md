# Renderer preview template

Empat schema di `../templates` sekarang menggunakan versi 1.2.0, dengan pasangan `.layout.json`.
JSON lama hanya mendefinisikan field; layout render dan field isi proposal/LPJ yang sebelumnya kosong
ditambahkan. DOCX asli tetap tidak diubah dan menjadi referensi struktur, bukan file yang dikonversi
oleh Microsoft Word/LibreOffice. PDF native memakai PDFsharp 6.2.4 yang sudah ada (lisensi MIT).

Layout mempertahankan isi surat peminjaman, tabel dua baris barang, cover/isi/lampiran proposal dan LPJ,
serta lembar pengesahan. Tata letak PDF ditata ulang secara terkontrol dalam A4, bukan salinan piksel
Word. Kop memakai letterhead resmi PENS mengikuti DOCX sumber: logo dari
`templates/assets/kop-pens-logo.png` plus teks kementerian, identitas kampus, dan alamat/kontak
(teks dicetak oleh renderer, gambar hanya dibaca dari aset server tepercaya). Teks PDF memakai font
Times New Roman (fallback Linux: Liberation Serif/DejaVu Serif/FreeSerif). Contoh identitas pribadi,
rekening, NIK, evaluasi, anggaran, jadwal dan dokumentasi lama dari DOCX LPJ tidak disalin ke hasil,
dan layout tidak mengklaim logo/alamat organisasi lain. Lampiran berupa teks schema; unggahan foto/bukti pembayaran dan
lampiran PDF gabungan belum termasuk renderer ini.

## Nilai dari server

- Nomor preview selalu DRAFT; nomor resmi belum terbit. Nama kampus diisi server.
- Nama organisasi dan kode/nama fasilitas berasal dari katalog database, bukan teks bebas.
- Nama peserta, posisi dan NRP/NIP berasal dari akun/routing. NRP/NIP yang belum diprovisioning diberi
  label belum tersedia, bukan diisi angka contoh.
- Field signatureEvidence tidak menerima QR dari client. Preview hanya memiliki placeholder.
- Slot server dibuat untuk seluruh chain aktif: 5 proposal/LPJ, 6 barang/PS/SAW, 7 D3/D4/lapangan.
  Lembar pengesahan dapat lebih dari satu halaman agar QR minimal 100 point dan teks tidak bertumpuk.
- Field participant pada susunan panitia juga diisi dari routing. Field user required divalidasi sebelum
  job dibuat. Frontend membaca schema terbaru, bukan hardcode daftar field lama.

## Job dan penyimpanan

Migrasi AddLetterPreviewJobs menambahkan tabel saja. Permintaan identik pada revisi yang sama memakai
job yang sama; batas 20 kombinasi preview per revisi. Input lengkap dan layout dibekukan dalam job.
Worker menggunakan FOR UPDATE SKIP LOCKED dan lease token, timeout render, maksimum 30 halaman/10 MB,
pemulihan setelah restart serta maksimum tiga percobaan. Failed dapat diantrekan ulang dengan POST yang
sama selama batas retry belum tercapai. Draft tidak dihapus dan tidak mendapat nomor baru saat gagal.
Job yang kalah oleh revisi/status surat baru menjadi Superseded.

Konfigurasi `Preview`: Enabled=true, PollSeconds=2, RenderTimeoutSeconds=20, LeaseSeconds=60.
Worker berjalan di backend; deployment harus mengaktifkan proses yang tetap hidup. Font Arial regular/bold
di Windows atau DejaVu/Liberation/FreeSans regular/bold di Linux wajib tersedia; kegagalan font menjadi
Failed, bukan PDF kosong. Tidak ada script/HTML/remote fetch dalam renderer.

Download memeriksa pemilik dan integritas hash. Submit hanya menerima hasil Ready dan slot yang sama
dengan job server; perubahan resource/peserta/asset template menuntut Generate ulang. Data terselesaikan,
layout hash, versi renderer dan PreviewJobId masuk snapshot pengajuan. Generate tidak membuat task/evidence.

## Pengujian

Jalankan `dotnet test tests/SignIt.Signatures.Tests/SignIt.Signatures.Tests.csproj`. Tes renderer membuat
contoh lokal di `.data/renderer-qa` (diabaikan Git). Tes kategori Postgres dilewati bila
`SIGNIT_TEST_CONNECTION` tidak tersedia; connection wajib menunjuk database terpisah berakhiran `_tests`.
Jalankan kategori tersebut setelah membuat database pengujian; fixture menerapkan migrasi dan seed katalog/
akun demo tanpa mereset database aplikasi. Tes mencakup empat template dan chain 5/6/7, duplikasi Generate,
otorisasi, stale draft/resource, manipulasi slot, download private, hash dan submit idempoten.
Uji tambahan mencakup dua Generate bersamaan, field wajib kosong, renderer gagal dan retry pada job yang sama.
`scripts/test-template-preview.ps1` menguji login, schema, Generate 202, polling Ready, PDF 200/no-store,
dan submit melalui HTTP. Jalankan hanya pada API lokal yang memakai database pengujian.

Contoh PDF diperiksa melalui pdfplumber serta render PNG dengan PDFium. Pemeriksaan kerangka PDF/koordinat
saja tidak membuktikan layout visual. Preview bukan surat selesai; finalisasi signature dijelaskan di bawah.
Integrasi reservasi dan email workflow masih merupakan pekerjaan terpisah.

Finalisasi sekarang dijalankan worker setelah transaksi evidence terakhir selesai. Overlay mendukung
PNG QR aplikasi melalui ekspansi piksel lossless, dan renderer final memakai input preview yang dibekukan.
Label DRAFT diganti label final dan nomor referensi SGN, tanpa mengubah review/revisi yang disetujui.
Tes mencakup overlay nyata, retry, hash dan download privat; empat PDF final diperiksa dengan ekstraksi
teks dan rendering PDFium. Nomor resmi kampus belum ditetapkan. Lihat signature-finalization.md.

Referensi adapter: https://docs.pdfsharp.net/PDFsharp/Topics/Start/First-PDF.html
Lisensi: https://docs.pdfsharp.net/General/License/License.html
