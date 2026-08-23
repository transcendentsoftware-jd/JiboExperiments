# Azure Infra

This folder holds the managed Azure deployment shape for Open Jibo.

Current split:

- `foundation/`
  - creates the shared Azure resources such as Key Vault, ACR, Log Analytics, and storage
  - assigns the deploying principal Key Vault Secrets Officer when its object ID is supplied so the deploy script can seed Key Vault secrets after deployment
- `container-apps/`
  - deploys the Open Jibo Container Apps runtime
  - reads runtime secrets from Key Vault via managed identity after the deploy scripts seed the app secrets from the vault
  - includes the knowledge-search backend config so hosted deployments can enable Wolfram Alpha, ChatGPT, or other supported AI backends consistently

The managed deployment workflow uses the foundation template first, then publishes the image, then deploys the app template, then runs migrations and smoke checks.
For the staging-first normalized persistence release procedure, use
[../../docs/managed-persistence-deployment-runbook.md](../../docs/managed-persistence-deployment-runbook.md).
