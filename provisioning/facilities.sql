BEGIN;
INSERT INTO facilities ("Id", "Code", "Name", "RequiresDagri")
SELECT md5('signit:facility:' || code)::uuid, code, name, dagri
FROM (VALUES ('PS','Pasca Sarjana',false),('SAW','SAW',false),('D3','Gedung D3',true),('D4','Gedung D4',true),
('LAPANGAN_MERAH','Lapangan Merah',true),('LAPANGAN_FUTSAL','Lapangan Futsal',true),('LAPANGAN_BASKET','Lapangan Basket',true)) AS v(code,name,dagri)
ON CONFLICT ("Code") DO NOTHING;

INSERT INTO facility_resources ("Id","FacilityId","Code","Floor","IsBookable")
SELECT md5('signit:resource:' || f."Code" || ':' || r.code)::uuid, f."Id", r.code, r.floor, true
FROM facilities f JOIN (
 SELECT 'SAW' AS facility, 'SAW ' || lpad(floor::text,2,'0') || '.' || lpad(room::text,2,'0') AS code, floor
 FROM generate_series(1,11) floor CROSS JOIN LATERAL generate_series(5,
 CASE WHEN floor=1 THEN 5 WHEN floor IN (2,3) OR floor%2=1 THEN 8 ELSE 10 END) room
 UNION ALL SELECT 'PS', 'PS ' || lpad(floor::text,2,'0') || '.' || lpad(room::text,2,'0'), floor
 FROM generate_series(3,11) floor CROSS JOIN generate_series(8,10) room WHERE floor <> 6
 UNION ALL SELECT 'PS', 'PS 06 AUDITORIUM', 6
 UNION ALL SELECT 'PS', 'PS 06 MINI THEATER', 6
 UNION ALL SELECT 'D3', 'D3 THEATER', NULL::integer
 UNION ALL SELECT 'D4', block || floor::text || lpad(room::text,2,'0'), floor
 FROM (VALUES ('A'),('B')) blocks(block)
 CROSS JOIN generate_series(1,3) floor CROSS JOIN generate_series(1,7) room
 UNION ALL SELECT code, name, NULL::integer FROM (VALUES ('LAPANGAN_MERAH','Lapangan Merah'),('LAPANGAN_FUTSAL','Lapangan Futsal'),('LAPANGAN_BASKET','Lapangan Basket')) v(code,name)
) r ON r.facility=f."Code"
ON CONFLICT ("FacilityId","Code") DO NOTHING;

INSERT INTO facility_resources ("Id","FacilityId","Code","Floor","IsBookable")
SELECT md5('signit:resource:' || "Code" || ':LOBBY')::uuid, "Id", "Code" || ' LOBBY', 0, false
FROM facilities WHERE "Code" IN ('PS','SAW') ON CONFLICT ("FacilityId","Code") DO NOTHING;
COMMIT;
