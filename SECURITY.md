# Security

OpenBook keeps a business's books, its customers' and vendors' details, bank connections and sign-ins, so security problems matter a great deal. Thank you for reporting one.

## Reporting a problem

**Please don't open a public issue, pull request or discussion for a security problem.** Report it privately instead: on GitHub, open the repository's **Security** tab and choose **Report a vulnerability**. Only the maintainers see the report.

Say what you can of:

- what an attacker could do, and who they'd need to be (someone on the internet, a signed-in user with a given role, someone with the server or the company file);
- the steps, or a proof of concept, against a test company, never real books;
- the version (Settings → System shows it), the install (Desktop or Server) and the database (SQLite or PostgreSQL).

We'll answer as soon as we can, usually within a week, and keep you told while it's fixed. Once a fixed release is out, we'll publish an advisory and credit you if you'd like to be named.

## What counts

Anything that breaks what OpenBook promises, for example:

- getting in without signing in, or doing more than your role or permission set allows;
- reading or changing another company's data, a customer portal, a payment link or an invitation that isn't yours;
- changing the books so they no longer balance, changing posted entries, or changing the audit trail without the books check noticing;
- reading secrets OpenBook keeps protected (passwords, two-factor secrets, API keys, bank and payment credentials);
- a file people can upload or import (attachments, statements, migration files) that runs code or reaches other systems;
- the desktop updater installing something that wasn't released by OpenBook.

Out of scope: problems that need someone who already controls the server, the database or the company file (they can do anything; the audit chain shows what they changed), and reports from automated scanners without a way to exploit what they found.

## Supported versions

Fixes go into the latest release. Please check a problem against it (or `master`) before reporting.

## Running OpenBook safely

The admin guides cover what's needed: [the server guide](docs/admin/server.md) (HTTPS behind a proxy, the Data Protection keys, backups) and [the desktop guide](docs/admin/desktop.md).
