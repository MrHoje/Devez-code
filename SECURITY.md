# Security policy

Do not open an issue with credentials, access tokens, private keys, passwords,
or personal data. Report a suspected vulnerability privately to the project
maintainer and include only the minimum information needed to reproduce it.

This repository must never contain production credentials. Local configuration,
certificates, and credential files are excluded by `.gitignore`; use an
untracked `.env` or an operating-system credential store for development-only
secrets.
