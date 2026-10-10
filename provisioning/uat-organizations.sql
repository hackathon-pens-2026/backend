BEGIN;
INSERT INTO organizations ("Id", "Scope", "Name", "Kind", "IsActive") VALUES
('ac202610-0010-4000-8000-000000000001', 'uat-himpunan', 'UAT Himpunan (Pengujian)', 'Himpunan', true),
('ac202610-0010-4000-8000-000000000002', 'uat-organisasi', 'UAT Organisasi (Pengujian)', 'Organisasi', true)
ON CONFLICT ("Scope") DO NOTHING;
COMMIT;
