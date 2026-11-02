-- Storage foundation only: no runtime registration, grants, or automatic lease acquisition.
CREATE TABLE IF NOT EXISTS public.HubChannelLeases
(
    TokenDigest BYTEA NOT NULL CHECK (pg_catalog.octet_length(TokenDigest)=32),
    Channel SMALLINT NOT NULL CHECK (Channel IN (1,2)),
    Generation BIGINT NOT NULL CHECK (Generation > 0),
    OwnerId UUID NOT NULL CHECK (OwnerId <> '00000000-0000-0000-0000-000000000000'::uuid),
    ExpiresUtc TIMESTAMPTZ NOT NULL CHECK (pg_catalog.isfinite(ExpiresUtc)),
    PRIMARY KEY (TokenDigest, Channel)
);

-- 1 acquire (generation must be zero), 2 renew, 3 release, 4 check.
-- Empty result means stale/expired ownership. Provider errors are never permission to proceed.
CREATE OR REPLACE FUNCTION public.ManageHubChannelLease(
    p_operation SMALLINT, p_digest BYTEA, p_channel SMALLINT, p_owner UUID, p_generation BIGINT)
RETURNS TABLE (LeaseGeneration BIGINT, LeaseExpiresUtc TIMESTAMPTZ)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    v_now TIMESTAMPTZ;
BEGIN
    IF p_operation IS NULL OR p_operation NOT IN (1,2,3,4) OR
       p_digest IS NULL OR pg_catalog.octet_length(p_digest)<>32 OR
       p_channel IS NULL OR p_channel NOT IN (1,2) OR
       p_owner IS NULL OR p_owner='00000000-0000-0000-0000-000000000000'::uuid OR
       p_generation IS NULL OR
       (p_operation=1 AND p_generation<>0) OR (p_operation<>1 AND p_generation<1) THEN
        RAISE EXCEPTION 'Hub channel lease request is invalid' USING ERRCODE='22023';
    END IF;

    -- Serialize even first acquisition; collisions only serialize unrelated slots.
    -- Include channel so listen and proactive never replace each other's ownership.
    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(
        pg_catalog.encode(p_digest,'hex') || ':' || p_channel::text, 682351718));
    v_now := pg_catalog.clock_timestamp();
    IF p_operation=1 THEN
        RETURN QUERY
        INSERT INTO public.HubChannelLeases AS lease (TokenDigest,Channel,Generation,OwnerId,ExpiresUtc)
        VALUES (p_digest,p_channel,1,p_owner,v_now+INTERVAL '30 seconds')
        ON CONFLICT (TokenDigest,Channel) DO UPDATE
        SET Generation=lease.Generation+1, OwnerId=p_owner, ExpiresUtc=v_now+INTERVAL '30 seconds'
        RETURNING lease.Generation,lease.ExpiresUtc;
    ELSIF p_operation=2 THEN
        RETURN QUERY UPDATE public.HubChannelLeases AS lease
        SET ExpiresUtc=v_now+INTERVAL '30 seconds'
        WHERE lease.TokenDigest=p_digest AND lease.Channel=p_channel AND
              lease.OwnerId=p_owner AND lease.Generation=p_generation AND lease.ExpiresUtc>v_now
        RETURNING lease.Generation,lease.ExpiresUtc;
    ELSIF p_operation=3 THEN
        -- Retain the generation tombstone: deletion/reset would permit ABA ownership reuse.
        RETURN QUERY UPDATE public.HubChannelLeases AS lease SET ExpiresUtc=v_now
        WHERE lease.TokenDigest=p_digest AND lease.Channel=p_channel AND
              lease.OwnerId=p_owner AND lease.Generation=p_generation AND lease.ExpiresUtc>v_now
        RETURNING lease.Generation,lease.ExpiresUtc;
    ELSE
        RETURN QUERY SELECT lease.Generation,lease.ExpiresUtc FROM public.HubChannelLeases AS lease
        WHERE lease.TokenDigest=p_digest AND lease.Channel=p_channel AND
              lease.OwnerId=p_owner AND lease.Generation=p_generation AND lease.ExpiresUtc>v_now;
    END IF;
END;
$$;
REVOKE ALL ON TABLE public.HubChannelLeases FROM PUBLIC;
REVOKE ALL ON FUNCTION public.ManageHubChannelLease(SMALLINT,BYTEA,SMALLINT,UUID,BIGINT) FROM PUBLIC;
COMMENT ON TABLE public.HubChannelLeases IS
    'Opaque token/channel generations for future socket fencing; retain tombstones; no raw bearer tokens or ownership claims.';
