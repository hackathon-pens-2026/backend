# Akun UAT server

Fixture ini opt-in untuk database pengujian, bukan akun/mandat kampus nyata. Membuat 12 akun baru di domain `.example`, dua organisasi UAT dan 18 assignment berakhir 10 November 2026. Akun mahasiswa tanpa assignment untuk pengujian penolakan akses. Tidak menambah approver ke scope lama agar routing tidak ambigu; password akun lama tidak diubah.

Password awal dibaca dari `SIGNIT_UAT_PASSWORD`, tidak disimpan dalam repository. Akun baru di-hash oleh AuthProvisioner, diaudit, dan QR disimpan langsung dalam storage server yang dipakai backend. Rerun tidak mereset password akun yang sudah ada. Kegagalan QR setelah commit akun dapat dicoba ulang dengan volume storage yang sama.

Pastikan kode backend server sudah terbaru, migrasi tersedia dan `../data/backend/storage` serta `keys` writable oleh UID 1654. Buat backup sebelum menjalankan. Dari `~/infrastructure/docker`:

```bash
sudo docker exec -i hackathon-postgres sh -c 'psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' < ../../backend/provisioning/uat-organizations.sql
read -r -s -p 'Password awal akun UAT (minimal 12 karakter): ' SIGNIT_UAT_PASSWORD
echo
sudo env SIGNIT_UAT_PASSWORD="$SIGNIT_UAT_PASSWORD" docker compose --env-file secrets.env -f docker-compose.yml -f ../../backend/provisioning/uat-compose.yml run --rm --no-deps uat-provisioner
unset SIGNIT_UAT_PASSWORD
```

Periksa output provisioning 12 akun baru (0 jika rerun). Di pgAdmin:

```sql
SELECT u."Email", u."IsActive", COUNT(DISTINCT a."Id") AS assignments,
       COUNT(DISTINCT q."Id") AS qr
FROM auth_users u
LEFT JOIN auth_user_assignments a ON a."UserId"=u."Id"
LEFT JOIN sig_user_qrs q ON q."OwnerUserId"=u."Id" AND q."Status"='Active'
WHERE u."Email" LIKE 'uat.%@demo.signit.example'
GROUP BY u."Email", u."IsActive" ORDER BY u."Email";
```

UAT tidak menjalankan aksi approval atau pengiriman email dengan sendirinya. `.example` tidak menerima email: uji email memerlukan sandbox/penerima nyata terkontrol dari tim email, bukan mengganti semua alamat secara sembarang. Login/mutasi melalui HTTP publik tidak aman; gunakan HTTPS atau tunnel untuk pengujian.

Cakupan akun: pengajuan empat template, chain Himpunan/Organisasi, signer Ketua Pelaksana/Ketua Organisasi, approver Pembina/Kemahasiswaan/MinatBakat/Dagri/BAAK/Wadir3/Wadir2, dan akses tanpa assignment. Delegasi memerlukan kandidat tambahan sesuai kebijakan; akun ini bukan bukti fitur delegasi atau seluruh fitur berhasil. UI manajemen/refresh/finalisasi masih mengikuti status integrasi frontend saat ini.
