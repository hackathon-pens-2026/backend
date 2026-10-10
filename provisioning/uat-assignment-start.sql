-- Controlled correction: midnight Asia/Jakarta, not midnight UTC.
-- Only UAT rows with the original start date are touched; reruns do nothing.
BEGIN;
WITH corrected AS (
    UPDATE public.auth_user_assignments a
    SET "ValidFrom" = TIMESTAMPTZ '2026-10-09 17:00:00+00'
    FROM public.auth_users u
    WHERE a."UserId" = u."Id"
      AND u."Id"::text LIKE 'aa202610-0010-4000-8000-%'
      AND u."Email" LIKE 'uat.%@demo.signit.example'
      AND a."Id"::text LIKE 'ab202610-0010-4000-8000-%'
      AND a."Scope" IN ('uat-himpunan', 'uat-organisasi')
      AND a."ValidFrom" = TIMESTAMPTZ '2026-10-10 00:00:00+00'
    RETURNING a."UserId"
)
INSERT INTO public.auth_audits ("Id", "UserId", "Action", "AtUtc", "IpAddress", "UserAgent", "CorrelationId")
SELECT gen_random_uuid(), "UserId", 'auth.uat_assignment_start_corrected', CURRENT_TIMESTAMP,
       NULL, NULL, 'uat-start-20261010-AsiaJakarta'
FROM corrected GROUP BY "UserId";
COMMIT;
