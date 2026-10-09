BEGIN;
INSERT INTO organizations ("Id", "Scope", "Name", "Kind", "IsActive")
SELECT md5('signit:organization:' || scope)::uuid, scope, name, kind, true
FROM (VALUES
('himpunan-informatika-sains-data','Himpunan Mahasiswa Teknik Informatika dan Sains Data','Himpunan'),
('himpunan-elektronika','Himpunan Mahasiswa Teknik Elektronika','Himpunan'),
('himpunan-elektro-industri','Himpunan Mahasiswa Teknik Elektro Industri','Himpunan'),
('himpunan-komputer','Himpunan Mahasiswa Teknik Komputer','Himpunan'),
('himpunan-telekomunikasi','Himpunan Mahasiswa Teknik Telekomunikasi','Himpunan'),
('himpunan-sistem-pembangkit-energi','Himpunan Mahasiswa Sistem Pembangkit Energi','Himpunan'),
('himpunan-mekatronika','Himpunan Mahasiswa Mekatronika','Himpunan'),
('himpunan-multimedia-broadcasting','Himpunan Mahasiswa Multi Media dan Broadcasting','Himpunan'),
('himpunan-game-teknologi','Himpunan Mahasiswa Game Teknologi','Himpunan'),
('bem','BEM','Organisasi'),('lmb','LMB','Organisasi'),('ukki','UKKI','Organisasi')
) v(scope,name,kind)
ON CONFLICT ("Scope") DO NOTHING;
COMMIT;
