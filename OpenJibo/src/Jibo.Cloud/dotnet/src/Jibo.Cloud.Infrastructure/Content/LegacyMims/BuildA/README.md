# Build A Legacy Mim Seed

This folder holds the first checked-in Build A legacy MIM seed set.

Importer rules:

- each `.mim` file is parsed as JSON
- XML-style tags and `${placeholder}` tokens are stripped into spoken text
- Build A uses declarative prompt packs only
- imported prompts are merged into the existing in-memory catalog

The goal is to get immediate personality value from source-backed legacy content while keeping the current runtime surface unchanged.
