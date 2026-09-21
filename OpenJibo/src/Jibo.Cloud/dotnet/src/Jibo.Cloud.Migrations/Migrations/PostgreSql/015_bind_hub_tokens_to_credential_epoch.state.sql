-- Credential-valid legacy requests may bind the issued Hub token to the
-- account credential epoch. This is not robot identity or pairing proof.

ALTER TABLE Accounts
    ADD COLUMN IF NOT EXISTS CredentialEpoch BIGINT NOT NULL DEFAULT 1;

ALTER TABLE Accounts
    DROP CONSTRAINT IF EXISTS CK_Accounts_CredentialEpoch;

ALTER TABLE Accounts
    ADD CONSTRAINT CK_Accounts_CredentialEpoch CHECK (CredentialEpoch > 0);
