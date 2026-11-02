# Installing and starting OpenBook

OpenBook runs as a small web server. You start it on a computer (your own, or a server for the whole office), then use it from a web browser. Nothing is sent to anyone else: your books live in a single *company file* on that computer, or in a PostgreSQL database if you choose one.

## Choose how to run it

| You are… | Run it | Your data is in |
| --- | --- | --- |
| One person on one computer | From source with .NET, or the Docker image with SQLite | `openbook.db`, a single file |
| A team sharing the books | Docker Compose with PostgreSQL, on a server | A PostgreSQL database |

You can start with the single file and move to a server later.

### On your own computer (from source)

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). In the OpenBook folder, run:

```bash
dotnet run --project src/OpenBook.Web
```

The first start creates `openbook.db` in the folder you ran it from. Open the address it prints (usually `http://localhost:5135`) in your browser.

### With Docker, single file

```bash
docker build -t openbook .
```

```bash
docker run -p 8080:8080 -v openbook-data:/data openbook
```

Then open `http://localhost:8080`. The company file is kept in the `openbook-data` volume, so it survives restarts and upgrades.

### On a server, with PostgreSQL

The repository's `docker-compose.yml` starts OpenBook and PostgreSQL 17 together:

```bash
docker compose up -d
```

Before you do, change both `change-me` passwords in `docker-compose.yml` to the same new password. Open `http://localhost:8080` (or the server's address). For anything beyond your own office network, put an HTTPS reverse proxy such as Caddy, nginx or Traefik in front of it.

> In-app backups (see [People, backups and safety](08-users-and-backups.md)) work with the single company file. With PostgreSQL, back up the database with `pg_dump`.

## Set up your company

The first time you open OpenBook it shows **Set up your company**. Fill in three parts:

**Moving from QuickBooks?** Before filling anything in, choose your export under **Export from**, pick the file and select **Read the file**. OpenBook fills in the company details it finds (name, legal name, address, phone, email, fiscal year, currency, and the legal structure from QuickBooks Desktop's tax form) and says what the file holds. QuickBooks Desktop qbXML files with a company query, QuickBooks Online files with CompanyInfo and GnuCash books carry these details; IIF and CSV exports don't, so type them in. The button then reads **Create company and import**: the company is created and everything in the file is imported with it, or, if any row has a problem, nothing is created and the problems are listed. See [Moving in](24-moving-in.md#importing-the-history) for what to export.

**1. Company**

- **Company name** is what appears on screens; **legal name** (optional) is for forms.
- **Legal structure** (sole proprietor, partnership, corporation, nonprofit and so on) picks the starter chart of accounts and the tax form your accounts are mapped to.
- **Which menu suits your work?** picks an *edition*: General, Professional services, E-commerce or Nonprofit. It only changes the order of the menu and the home page shortcuts; every feature stays available. You can change it later in **Settings → Company details**.
- **How should OpenBook look?** picks the view people start with: the *simple view* (a short menu bar by task and a home page that walks through each step, suggested when one person runs the business) or the *full view* (every area in the menu). Both work on the same books, and anyone can switch at any time. See [The simple view](28-simple-view.md).
- **What do you sell?** (services, products or both), **I keep products in stock** and **I charge sales tax**: in the simple view these set up your chart of accounts for you (see below). In the full view you choose a starter chart on the next page instead.
- **Email, phone and address** are optional; the address is printed on invoices.

**2. Books**

- **Fiscal year starts in**: the first month of your financial year. Most small businesses use January.
- **Home currency**: a three-letter code such as `USD`. **This can't be changed later.** OpenBook keeps books in one currency.

**3. Your sign-in**

Choose a username and a password of at least 8 characters. This first person is an administrator and can add everyone else later.

Select **Create company**. You're signed in and taken to the **Chart of accounts** page, which offers starter charts, or, after an import, shows the accounts that came in. In the simple view (without an import) the starter chart for your legal structure is set up for you, trimmed to what you sell (no product sales or cost of goods sold for a services business, an Inventory Asset account when you keep stock), and you go to the home page, or to **Sales tax** to add your tax agency if you said you charge it. The home page has a **Getting started** list that ticks off each step as you do it. If OpenBook runs on a server in another time zone (a hosted or Docker server usually runs on UTC), set your **Time zone** under **Settings → Company details** first: it decides which day "today" is for new documents, what's overdue, when the scheduled jobs run, and the times shown on pages (history, sign-ins, emails sent). The usual order is:

1. Pick a chart of accounts ([chapter 3](03-chart-of-accounts.md)).
2. Enter opening balances, if you're moving from other books.
3. Add customers, vendors and products and services, by hand or by importing a CSV file.
4. Start entering invoices, bills and bank transactions.

If you're moving from QuickBooks, GnuCash, Xero, Wave or FreshBooks, see [Moving in](24-moving-in.md): you can bring in your accounts, names and transactions at once with **Settings → Move in from another program**.

## Signing in and out

Go to OpenBook's address and sign in with your username and password, or with **Sign in with …** when your company uses single sign-on (see [Single sign-on](08-users-and-backups.md#single-sign-on)). You stay signed in on that browser while you keep using it; after 8 hours without activity you're asked to sign in again. **Sign out** is in the menu under your username, at the right of the top bar.

Select your username in the top bar, then **Password and sessions**, to change your password. The same page lists every browser and device signed in as you, with when each was last used. You can sign out any one of them, or **Sign out all other sessions** if you think someone else has your password. Changing your password signs out every other session too.

### Forgotten password

If another administrator can sign in, they can reset your password on the **Users** page. If nobody can, someone with access to the computer that runs OpenBook can reset it from the OpenBook folder:

```bash
dotnet run --project src/OpenBook.Web -- reset-password yourname
```

It asks for the new password twice. Add `--clear-two-factor` to turn off their two-factor sign-in as well, for someone who has also lost their phone and recovery codes.

### Two-factor sign-in

With two-factor sign-in, signing in takes your password and a 6-digit code from an authenticator app on your phone, so a stolen password isn't enough. See [Two-factor sign-in](08-users-and-backups.md#two-factor-sign-in).

## Upgrading

Stop OpenBook, get the new version, and start it again. It updates the company file or database on start-up. Take a backup first; it's one click on the **Backup** page.
