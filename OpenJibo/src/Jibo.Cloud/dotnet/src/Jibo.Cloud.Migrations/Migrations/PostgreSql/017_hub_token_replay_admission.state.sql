-- Strict admission is separate from observe-only replay telemetry. No automatic grants.
CREATE TABLE IF NOT EXISTS public.HubTokenReplayAdmissions
(
    ReplayDigest BYTEA PRIMARY KEY CHECK (pg_catalog.octet_length(ReplayDigest)=32),
    KeyVersion SMALLINT NOT NULL CHECK (KeyVersion > 0),
    SignedAtUtc TIMESTAMPTZ NOT NULL,
    ConsumedUtc TIMESTAMPTZ NOT NULL,
    ExpiresUtc TIMESTAMPTZ NOT NULL CHECK (ExpiresUtc > ConsumedUtc)
);
CREATE INDEX IF NOT EXISTS IX_HubTokenReplayAdmissions_ExpiresUtc
    ON public.HubTokenReplayAdmissions(ExpiresUtc);

CREATE OR REPLACE FUNCTION public.ConsumeHubTokenReplay(
    p_digests BYTEA[], p_versions SMALLINT[], p_signed_at TIMESTAMPTZ)
RETURNS BOOLEAN
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    v_now TIMESTAMPTZ;
    v_lock BIGINT;
    v_index INTEGER;
BEGIN
    IF p_digests IS NULL OR p_versions IS NULL OR p_signed_at IS NULL OR
       NOT pg_catalog.isfinite(p_signed_at) OR
       pg_catalog.array_ndims(p_digests) IS DISTINCT FROM 1 OR
       pg_catalog.array_ndims(p_versions) IS DISTINCT FROM 1 OR
       pg_catalog.array_lower(p_digests,1) IS DISTINCT FROM 1 OR
       pg_catalog.array_lower(p_versions,1) IS DISTINCT FROM 1 OR
       pg_catalog.cardinality(p_digests) NOT BETWEEN 1 AND 2 OR
       pg_catalog.cardinality(p_digests) <> pg_catalog.cardinality(p_versions) OR
       EXISTS (SELECT 1 FROM pg_catalog.unnest(p_digests) d WHERE d IS NULL OR pg_catalog.octet_length(d)<>32) OR
       EXISTS (SELECT 1 FROM pg_catalog.unnest(p_versions) v WHERE v IS NULL OR v<1) OR
       (SELECT pg_catalog.count(DISTINCT d) FROM pg_catalog.unnest(p_digests) d) <> pg_catalog.cardinality(p_digests) OR
       (SELECT pg_catalog.count(DISTINCT v) FROM pg_catalog.unnest(p_versions) v) <> pg_catalog.cardinality(p_versions) THEN
        RAISE EXCEPTION 'Hub replay admission batch is invalid' USING ERRCODE='22023';
    END IF;

    -- Total order over actual lock IDs avoids deadlocks even if lock hashes collide.
    -- Overlapping current/previous slots serialize as one admission across replicas.
    FOR v_lock IN
        SELECT DISTINCT pg_catalog.hashtextextended(pg_catalog.encode(d,'hex'), 682351714)
        FROM pg_catalog.unnest(p_digests) d ORDER BY 1
    LOOP
        PERFORM pg_catalog.pg_advisory_xact_lock(v_lock);
    END LOOP;
    v_now := pg_catalog.clock_timestamp();
    -- Recheck AFTER lock acquisition. A request that expires while queued cannot be admitted.
    IF p_signed_at <= v_now-INTERVAL '5 minutes' OR p_signed_at > v_now+INTERVAL '5 minutes' THEN
        RETURN FALSE;
    END IF;
    IF EXISTS (SELECT 1 FROM public.HubTokenReplayAdmissions a
               WHERE a.ReplayDigest=ANY(p_digests) AND a.ExpiresUtc>v_now) THEN
        RETURN FALSE;
    END IF;
    FOR v_index IN 1..pg_catalog.cardinality(p_digests) LOOP
        INSERT INTO public.HubTokenReplayAdmissions AS a
            (ReplayDigest,KeyVersion,SignedAtUtc,ConsumedUtc,ExpiresUtc)
        VALUES (p_digests[v_index],p_versions[v_index],p_signed_at,v_now,p_signed_at+INTERVAL '5 minutes')
        ON CONFLICT (ReplayDigest) DO UPDATE
        SET KeyVersion=EXCLUDED.KeyVersion, SignedAtUtc=EXCLUDED.SignedAtUtc,
            ConsumedUtc=EXCLUDED.ConsumedUtc, ExpiresUtc=EXCLUDED.ExpiresUtc
        WHERE a.ExpiresUtc<=v_now;
    END LOOP;
    WITH expired AS
    (
        SELECT a.ReplayDigest FROM public.HubTokenReplayAdmissions a
        WHERE a.ExpiresUtc<=v_now AND NOT (a.ReplayDigest=ANY(p_digests))
        ORDER BY a.ExpiresUtc LIMIT 128 FOR UPDATE SKIP LOCKED
    )
    DELETE FROM public.HubTokenReplayAdmissions a USING expired e WHERE a.ReplayDigest=e.ReplayDigest;
    RETURN TRUE;
END;
$$;
REVOKE ALL ON TABLE public.HubTokenReplayAdmissions FROM PUBLIC;
REVOKE ALL ON FUNCTION public.ConsumeHubTokenReplay(BYTEA[],SMALLINT[],TIMESTAMPTZ) FROM PUBLIC;
COMMENT ON TABLE public.HubTokenReplayAdmissions IS
    'Strict Hub issuance replay consumption: environment-keyed digests only; separate from replay observation.';
