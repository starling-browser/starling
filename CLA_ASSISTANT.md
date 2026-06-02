# CLA Assistant Setup

Starling uses the hosted
[`cla-assistant/cla-assistant`](https://github.com/cla-assistant/cla-assistant)
service for inbound contributions. The CLA text lives in [`CLA.md`](CLA.md).

The hosted CLA Assistant service expects the agreement to be stored as a GitHub
Gist, then linked to the repository or organization in the CLA Assistant UI.

## Setup

1. Create a public GitHub Gist named `CLA.md`.
2. Copy the contents of this repo's `CLA.md` into that Gist.
3. Optional: add a second Gist file named `metadata` with the JSON below.
4. Visit <https://cla-assistant.io/> and link the Starling repository to the
   Gist.
5. Mark the CLA Assistant status check as required before merge.
6. Add bot accounts such as `dependabot[bot]`, `github-actions[bot]`, and
   `copilot-swe-agent[bot]` to the CLA Assistant allowlist.

Repeat this setup for `starling-regexp`, since it is a separate repository.

## Suggested Metadata

```json
{
  "name": {
    "title": "Full name",
    "type": "string",
    "githubKey": "name",
    "required": true
  },
  "email": {
    "title": "Email",
    "type": "string",
    "githubKey": "email",
    "required": true
  },
  "capacity": {
    "title": "Signing capacity",
    "type": {
      "enum": [
        "I am signing for myself.",
        "I am signing for my employer or organization."
      ]
    },
    "required": true
  },
  "authority": {
    "title": "I have authority to submit this contribution under the CLA.",
    "type": "boolean",
    "required": true
  }
}
```
