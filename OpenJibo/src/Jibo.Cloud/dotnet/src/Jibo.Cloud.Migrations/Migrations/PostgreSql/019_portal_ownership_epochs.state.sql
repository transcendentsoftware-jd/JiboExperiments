-- Nullable columns retain a deterministic identity for pre-upgrade ownership rows.
-- Legacy integration identity is frozen when an owner changes.
ALTER TABLE UserDevices ADD COLUMN IF NOT EXISTS OwnershipEpoch TEXT;
ALTER TABLE UserDevices ADD COLUMN IF NOT EXISTS LegacyIntegrationEpoch TEXT;
