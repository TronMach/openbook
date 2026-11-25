# Privacy

OpenBook is a program you run yourself, on your own computer or server. Your books stay in your company file or your own database. The OpenBook project doesn't run a service behind it: it collects no telemetry, usage statistics or crash reports, and the program never sends your books to the project or its maintainers.

OpenBook contacts other services only in the cases below. Apart from the update check, each one happens only after someone in your company sets it up, and you can switch each one off again. Each service handles what it receives under its own privacy policy.

## Update check

- **OpenBook Desktop** checks for a new version a minute after it starts and then once a day. It downloads `latest.json`, and the new installer when there is one, from the project's releases on GitHub. GitHub sees your computer's IP address and the OpenBook version, which is sent in the request (`OpenBook/x.y.z`). Nothing about your company is sent. Turn it off in **Settings → System → Updates**, or with `OpenBook:Updates:Check` set to `false`.
- **OpenBook Server for Windows** checks the same way, but downloads only `latest.json`. Turn it off in the same places.
- **OpenBook Server** with Docker doesn't check unless `OpenBook:Updates:Check` is set to `true`.

## Services you connect

| What | Where it goes | What is sent |
|---|---|---|
| Sending email (invoices, statements, reminders, alerts) | The mail server you enter in **Settings → Email** | The messages and their attachments |
| Microsoft's sign-in for a mailbox | Microsoft (login.microsoftonline.com), with your own app registration | The sign-in exchange, and requests for access tokens |
| Bills by email | The mail server you enter in **Settings → Bills by email** | The sign-in, and requests for new messages in that mailbox; nothing in it is changed |
| Sending text messages | Twilio, with your Twilio account | The customer's mobile number and the message |
| Bank connections | SimpleFIN Bridge or Plaid, with the access you give them | Requests for your accounts' balances and transactions |
| Online payments, online giving and the customer portal | Stripe, PayPal or Square, with your own keys | The amounts and descriptions of what customers pay, and their email address; your customers enter their card details on the provider's own page, never in OpenBook (a card saved for automatic payment stays at Stripe; OpenBook keeps its last four digits and expiry) |
| Sales channels | Stripe or Shopify, with your key or access token | Requests for payouts and their transactions |
| Reading documents and receipts with Claude | Anthropic, with your API key | The file being read (a PDF, image or text) |
| Google Fonts for printed forms | Google Fonts | The name of the font family someone adds; the font is downloaded once |
| Single sign-on | The sign-in provider you set up (for example Microsoft Entra ID or Google) | The usual sign-in exchange, in the person's browser |
| Webhooks | The addresses you enter | The changes those webhooks are set to send |
| Telemetry (Server, off by default) | The OpenTelemetry (OTLP) collector you name | Timings of requests, outgoing calls and background jobs; paths have record ids and tokens replaced and query string values left out |
| Backups to a folder | The folder you choose, which may be a synced cloud folder | The backup file |

Imports from other programs read files you upload. OpenBook doesn't sign in to those programs.

The **support file** (Settings → System → Download support file) is a ZIP of the set-up, recent problems and a week of logs that an admin can save and attach to a bug report. OpenBook makes it on request and sends it nowhere; addresses, usernames, passwords, keys and tokens are taken out, but names written into error messages can stay in, so read it before sending it.

## Your customers, vendors and donors

The people in your books are your business's to look after. OpenBook gives you the tools for it: exports of everything, an audit trail, roles and permission sets, and encryption of the company file and of stored keys. When someone asks for their data to be erased, an admin can erase a customer's, donor's or vendor's personal details, which keeps the documents tax law makes you keep, the audit log, and backups made before. What you collect, how long you keep it and what you tell people about it is up to you as the business that keeps the books.

## The project

Issues, pull requests and discussions on GitHub are public. Please don't put real company data in them. Report security problems privately, as [SECURITY.md](SECURITY.md) describes.

Changes to this statement are made in this file, so its history shows what changed and when.
