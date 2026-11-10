---
name: ui-ux-expert
description: Expert guidance for designing, building, reviewing and scoring user interfaces for ease of use. Use this skill whenever the work touches something a person will see or operate, including a new page, form, table, dashboard, menu, dialog, settings screen, onboarding flow, error message, empty state, mobile layout or component, and whenever the user asks to make something "easier", "cleaner", "more intuitive", "less confusing", "more consistent" or "more accessible", asks for a UX review or usability audit, or asks how good an interface is. Use it even when the request is phrased as a plain feature ("add a page that lists X", "let people edit Y"), because every such feature is an interface decision. It gives a working method, organizational patterns, handling of user preferences, the common challenges, and a measurable scorecard so that interface quality can be stated as numbers, not opinions.
---

# UI and ease-of-use expert

An interface is good when the people it is for can do what they came to do, quickly, without mistakes they can't undo, and without having to think about the interface itself. Everything below serves that. Looks matter only as far as they help: visual polish on a confusing flow is still a confusing flow.

Three rules sit above the rest.

1. **The project's own conventions win.** Before designing anything, read how the codebase already does it: its components, layout classes, wording, form patterns, and any project instructions. A page that follows the product's existing patterns is easier to use than a "better" page that works differently from its neighbours, because people have already learned the neighbours. Propose changing a convention separately; don't quietly fork it.
2. **Claims about quality need evidence.** "This is more intuitive" is not a finding. "The primary task took 9 clicks and now takes 4", "contrast was 3.2:1 and is now 7.1:1" are. Use the scorecard at the end.
3. **Stay inside the request.** Do what was asked, as well as it can be done, and no more. While working you will notice other things worth improving: a neighbouring page, a term you would have chosen differently, an old pattern. List those as recommendations for the person to decide on; don't do them. A small request answered with a large diff is harder to review and to trust, and it takes decisions away from the person who owns the product. Two cases come up often:
   - **Established words stay.** A label people already use ("Replenish", "Float") is something they have learned, and it appears in help pages, emails and other screens you haven't read. Explain a term where it confuses (a hint, a sentence saying what happens next) before replacing it. If you believe a rename is right, propose it and say what else it would touch.
   - **Touching a file is not a licence to rework it.** Adding a link to a page doesn't mean modernising the rest of that page.

**In the OpenBook repository**, read `references/openbook.md` before designing or reviewing anything. It lists the page skeletons, components, CSS classes, script behaviours, access and wording rules this product already has, the checks a page must pass, and how its stylesheet stands against the scorecard.

## Method

Work in this order. Skipping to layout before the first two steps is the most common cause of interfaces that look fine and work badly.

### 1. Who, what, where

Write down, in a few lines, before touching markup:

- **Who uses it** and what they already know. A bookkeeper, a developer and a first-time visitor need different words and different density. Use their vocabulary, never the data model's.
- **The top tasks**, ranked. Usually one to three tasks account for most use. Those get the shortest path and the most prominent controls; everything else may cost an extra click.
- **How often.** Daily-use screens should favour speed, density and keyboard use. Rare screens should favour explanation and safety, since nobody remembers how they work.
- **Where and on what.** Phone in one hand, a wide desktop monitor, a screen reader, a slow connection, bright sunlight.
- **What can go wrong and what it costs.** The more expensive the mistake, the more the design must prevent it or make it reversible.

### 2. Inventory what exists

Find the nearest existing screen that does a similar job and reuse its structure, components and wording. List the components available. Note the gaps you really need to fill. New patterns have a cost: each one is something else for people to learn and for the team to maintain.

### 3. Structure before surface

Decide what is on the page, in what order, and what is left off, before any styling.

- One page, one main purpose. If you can't title the page in a few plain words, it is doing too much.
- Order content by how often it is needed, not by the order of fields in the database.
- Group related things; separate unrelated things with space before lines or boxes.
- Decide the one primary action. One per view. Everything else is secondary or tucked away.

### 4. Design every state

The "happy path with realistic data" is one state of about eight. Unfinished states are where most real-world confusion comes from.

| State | What it must do |
|---|---|
| Empty (first use) | Say what belongs here and offer the action that fills it |
| Empty (no results) | Say nothing matched, show what was searched, offer a way to widen or clear |
| Loading | Show that something is happening; keep the layout stable so nothing jumps |
| Partial | Work with one item, with 10,000, with a 200-character name, with missing optional data |
| Error | Say what happened, why if known, and what to do next; keep everything the person typed |
| Success | Confirm what was done, in the place they are looking, and offer the likely next step |
| Disabled / no permission | Show that the thing exists and say why it isn't available, rather than hiding it without trace |
| Offline / slow / stale | Don't lose work; say when data may be out of date |

### Reviewing something that already exists

A review is read by someone deciding what to do next, so lead with the answer.

- Open with the verdict in two or three sentences: how good it is, and the one or two things that matter most.
- Then the findings, most serious first, each with where it is, why it costs the user something, its severity, and the fix. Group cosmetic points into one line instead of a finding each.
- Check each claim against the code or the screen before writing it. A wrong finding costs more trust than a missed one. Read the components the page uses, not only the page.
- Look for defects as well as design: the same figure shown with different values on two screens, a list in no particular order, an id used twice, a control left live for someone who can't use it, a link that goes somewhere other than its text says.
- Say what the page does well in a line or two, so it survives the changes.
- Keep it as short as the findings allow. One page rarely needs more than about 600 words plus the scorecard.
- Don't change code unless asked to.

### 5. Build, then verify by using it

Open the result and use it the way a person would. Do the top task end to end with the keyboard only. Shrink the window to phone width. Switch to dark mode. Zoom to 200%. Put in a long name, an empty list, a wrong value. Take screenshots when you can; reading markup is not the same as seeing the page. Then fill in the scorecard.

## Principles and why they hold

These are the reasons behind the patterns. When a situation isn't covered, reason from these.

- **Recognition beats recall.** People are far better at picking from what they see than remembering what to type. Show options, recent items, examples and current values. Pre-fill what you can work out.
- **Match what people already know (Jakob's law).** People spend most of their time in other software, so standard placements and behaviours (logo goes home, underlined text is a link, Esc closes) cost nothing to learn. Deviate only for a clear gain.
- **Fewer, clearer choices (Hick's law).** Decision time grows with the number and similarity of options. Cut, group, or set a good default. Don't solve it by hiding things people need often.
- **Big and near for what matters (Fitts's law).** The time to hit a target depends on its size and distance. Make primary actions large and close to where attention already is; keep destructive actions apart from them.
- **Good defaults do most of the work.** Most people never change a setting. The default is the design. Choose the one that is right for most people and safe for everyone.
- **Progressive disclosure.** Show what most people need now; put the rest one deliberate step away. This lets one screen serve beginners and experts. The hidden part must be easy to find and must open by itself when it holds something non-default, or people will miss what is set.
- **Visible status and prompt feedback.** Every action gets a visible response. Under about 0.1 s feels instant; up to 1 s keeps the flow of thought; beyond that show progress; beyond 10 s people leave, so let the work continue in the background and tell them when it's done.
- **Prevent errors first, then forgive them.** Constrain inputs, accept many formats, disable what can't work and say why. Prefer undo to "Are you sure?": people click through confirmations by habit, so a confirmation protects little, while undo actually recovers the mistake. Keep confirmations for what truly can't be undone, and name the consequence in the dialog.
- **Complexity doesn't vanish; someone carries it (Tesler's law).** If the software can work something out, it should, rather than asking the person. Every field is a cost.
- **Consistency.** The same thing looks and behaves the same everywhere, and different things look different. This covers words too: one name per concept, across pages, buttons, messages and help.
- **Hierarchy guides the eye.** Size, weight, colour and space say what matters. If everything is emphasised, nothing is. Squint at the page: the first thing you notice should be the most important.
- **Accessibility is ease of use for everyone.** Keyboard access, readable contrast, clear labels and large targets help the person with a disability, the person in sunlight, the person with a trackpad and the expert who never leaves the keyboard. Build it in from the start; it is far harder to add later.

## Organizational patterns

### Information architecture and navigation

- Organise by the user's tasks and nouns ("Customers", "Pay bills"), not the system's internals.
- Keep top-level navigation to about five to nine items, in a stable order. Menus that rearrange themselves destroy the muscle memory that makes them fast.
- Broad and shallow beats narrow and deep: people find things faster choosing from more visible labels than drilling through levels. Aim for most destinations within three steps.
- Always answer three questions on screen: where am I, how did I get here, where can I go. Titles, a highlighted current nav item and breadcrumbs for depth do this.
- Give several routes to the same place: navigation for browsing, search for knowing, links in context for flow, shortcuts for experts.
- Every view that shows a particular thing or filter gets its own address, so it can be bookmarked, shared, and survive the Back button and a refresh.
- Labels are plain nouns or verb phrases with strong "scent": a person should be able to predict what is behind the link. Avoid clever names and internal jargon.

### Page archetypes

Most screens are one of these. Use the standard shape; people recognise it.

- **Hub or home**: what needs attention now, shortcuts to the top tasks, then the rest. Not a wall of every feature.
- **List**: title, one primary "New" action, search and filters above, sortable columns, row links to detail, paging or "show more", a real empty state.
- **Detail**: identity and status at the top, primary actions beside the title, key facts first, related records below, history last.
- **Form (create or edit)**: see below.
- **Dashboard**: a few numbers that answer real questions, each linking to its detail; comparisons give numbers meaning.
- **Settings**: grouped by subject, searchable when long, each with a sentence saying what it does and its current value visible.
- **Wizard**: only for rare, ordered, multi-step tasks. Show the steps, allow going back without losing work, summarise before committing.

### Forms

Forms are where most effort and most errors happen.

- Ask only for what is needed now. Each removed field raises completion.
- One column, top to bottom. Multiple columns make people miss fields, except for tightly related short ones (city, state, postcode).
- Labels above fields, always visible. Placeholder text is not a label: it vanishes on typing, has poor contrast, and is often skipped by assistive technology.
- Size a field to its expected content; the width is a hint about what goes in.
- Mark the minority: if most fields are required, mark the optional ones, and the reverse.
- Pick the control from the number of choices: two to five mutually exclusive options visible as radio buttons or a segmented control; a select for a short known list; a searchable picker for long lists; a checkbox for a single on/off that takes effect on save; a switch only for something that takes effect immediately.
- Use the right input type and autocomplete hints, so phones show the right keyboard and browsers can fill in.
- Be liberal in what you accept (spaces in card numbers, either date order where unambiguous, pasted values) and strict in what you store.
- Validate when the person leaves a field, not on every keystroke and not only at submit. Put the message beside the field, say how to fix it, and at submit also summarise at the top and move focus to the first problem.
- Never clear the form on error. Losing typed work is the least forgivable failure.
- Buttons: the primary action is labelled with what it does ("Save invoice", not "Submit" or "OK"), placed consistently, visibly distinct from secondary actions, and guarded against double submission. Destructive actions look different and sit apart.
- Long forms: group under headings, save drafts automatically, and warn before leaving with unsaved changes.

### Tables and lists

- Text left-aligned, numbers right-aligned with consistent decimals so they compare at a glance, headings aligned with their column.
- The first column identifies the row and is the link to it.
- Default sort is the one most people want (usually newest first or by name). Show the current sort.
- Keep row actions few and consistent; put the rest behind a menu. Offer bulk actions when people act on many rows.
- Page or load progressively for long lists; show the total count.
- On small screens, let the table scroll inside itself or turn rows into cards, keeping the identifying column visible. The page itself must not scroll sideways.

### Feedback, messages and wording

- Put the message where the person is looking, in proportion to its importance: inline text for field issues, a banner for page-level news, a dialog only when the person must decide before continuing. Transient toasts suit confirmations only, since they vanish; never use one for an error or anything the person must read or act on.
- Error messages say what happened, in plain words, and what to do. No codes alone, no blame, no "invalid input".
- Write in sentence case, in the second person, with verbs on buttons. Put the important words first; people scan the first few words of each line.
- Be specific: "Delete 3 invoices" beats "Delete"; "Saved at 2:14 PM" beats "Success".
- Help belongs at the point of need: a short hint under the field, a link to the relevant help section, an example. Long instructions above a form go unread.

### Visual system

- Use a spacing scale (for example 4, 8, 12, 16, 24, 32, 48) and a type scale with few sizes. Consistent rhythm reads as quality and makes grouping clear.
- Body text around 16 px, line height about 1.4 to 1.6, line length about 45 to 75 characters.
- Colour carries meaning sparingly: one accent for primary actions and links, a small set for status. Never use colour as the only signal; pair it with text, an icon or a shape, since roughly one man in twelve has a colour vision deficiency.
- Define colours as named tokens (surface, text, muted, border, accent, danger) so themes and dark mode are a change of values, not a rewrite.
- Icons need text labels unless they are truly universal (search, close, menu). An unlabelled icon is a guess.
- Motion explains change (where something came from, what just updated) and stays short, about 150 to 300 ms. Decorative motion slows people down.

## User preferences

Respect what people have already told their device, remember what they tell you, and never make them say it twice.

**Read from the system and browser, and follow without asking:**

- Light or dark theme (`prefers-color-scheme`), with a manual override that persists.
- Reduced motion (`prefers-reduced-motion`): remove non-essential animation, parallax and auto-play.
- Higher contrast (`prefers-contrast`, forced colours mode): don't rely on background images or subtle borders to convey meaning.
- Text size and zoom: size in relative units so the person's setting applies; nothing breaks at 200% zoom.
- Language and region: dates, numbers, currency, first day of week, text direction. Format for the reader, and never build sentences by joining fragments, since word order differs between languages.
- Input method: touch, mouse, keyboard, voice, screen reader. Never require hover, a precise drag, or a gesture without an alternative.

**Remember per person:** theme, density, sort order and filters on lists they return to, collapsed sections, last-used values for repetitive entry, columns shown, the view they left. Returning to how they left it is one of the cheapest ways to make software feel easy.

**Rules for preferences:**

- A preference is not a substitute for a decision. Each option adds testing, support and a choice most people will never make. Add one only when real groups of users need different things; otherwise pick the right default.
- A personal preference changes presentation only. It must never change the data, the rules, or what someone is allowed to do.
- Store preferences that matter with the account so they follow the person across devices; device-local storage is for conveniences only, and the page must work when it is empty.
- Make a preference easy to find and to reverse, and show its effect immediately.
- Serve both ends of experience on the same screen: clear visible paths for beginners, and accelerators (keyboard shortcuts, bulk actions, type-ahead, saved views) for experts that stay out of a beginner's way.

## Challenges to expect

- **You are not the user.** You know the system's model; they don't. Anything "obvious" to its builder is suspect. Counter it by using their words, checking against real tasks, and where possible watching someone try. Five people attempting the top tasks reveal most serious problems.
- **Feature growth.** Every addition is reasonable alone and the sum is clutter. Counter it by ranking by frequency, disclosing progressively, and removing or merging as you add.
- **Beginners versus experts.** Simplifying for one can slow the other. Use layered design: sensible defaults, optional depth, accelerators.
- **Density versus clarity.** Professional tools need a lot on screen; whitespace everywhere wastes an expert's time. Dense is fine when it is aligned, grouped and consistent. Crowded is when it is none of those.
- **Consistency versus the best local solution.** A locally better pattern that differs from the rest of the product usually loses. Change the pattern everywhere or not at all.
- **Small screens and touch.** Less room, no hover, fat fingers, on-screen keyboards covering half the view. Design the narrow layout deliberately instead of hoping the wide one shrinks.
- **Real data.** Long names, missing values, zero items, ten thousand items, other alphabets, right-to-left text, text that grows 30 to 40% when translated. Test with ugly data, not sample data.
- **Asynchrony and failure.** Slow networks, double clicks, expired sessions, two people editing the same thing, a tab left open overnight. Decide what the person sees in each case.
- **Permissions.** Hiding what someone can't do makes the product feel different for every person and makes help impossible ("where is the button?"). Showing it disabled with a reason is usually clearer in pages; menus may hide what's unreachable to stay short.
- **Legacy habits.** Existing users have muscle memory. A redesign that is better for newcomers can be worse for them for a while. Change gradually, keep addresses and shortcuts working, and say what moved.
- **Dark patterns.** Tricks that push people into choices against their interest (hidden costs, confusing opt-outs, guilt-laden decline buttons) raise a short-term number and destroy trust. Don't build them.
- **Aesthetics masking problems.** Attractive interfaces are judged as easier to use even when they aren't. Check the numbers before trusting the impression, your own included.

## Goals and measurement

State quality as numbers. There are three tiers; use the ones the situation allows, and say which tier a figure came from. Tier 1 and 2 can always be done while building. Tier 3 needs real people or real usage data; if you don't have it, say so and don't invent it.

### Tier 1: measurable from the artifact itself

| Measure | Target |
|---|---|
| Text contrast | at least 4.5:1 for body text; 3:1 for large text (about 24 px, or 19 px bold) |
| Non-text contrast (control borders, icons, focus rings) | at least 3:1 against what's next to them |
| Pointer target size | at least 24 × 24 CSS px always; 44 × 44 or more for primary and touch controls |
| Keyboard | every interactive element reachable and operable; visible focus; logical order; no traps |
| Labels | 100% of inputs have a programmatic label; 100% of images and icon buttons have a text alternative |
| Reflow | no sideways page scroll at 320 CSS px wide; usable at 200% zoom |
| Colour independence | no information carried by colour alone |
| Motion | honours reduced-motion; nothing flashes more than 3 times a second |
| Automated accessibility scan (axe, Lighthouse or similar) | zero violations. Note that automated tools catch only a minority of issues, so this is a floor, not proof |
| Page speed (75th percentile) | largest content painted within 2.5 s; response to interaction within 200 ms; layout shift score 0.1 or less |
| Feedback latency | visible response to any action within 100 ms; progress indicator for anything over 1 s |
| Primary actions per view | exactly one |
| Steps for each top task | count clicks, fields and page loads; record before and after; fewer than the previous version or the nearest comparable flow |
| Required fields | each one justified; count before and after |
| States designed | all eight in the state table, for each view |
| Consistency | zero new one-off components or terms where an existing one fits |

The accessibility targets correspond to WCAG 2.2 level AA, which is the standard most organisations and regulations reference.

### Tier 2: expert review score

Rate the interface against each of the ten usability heuristics below. For each problem found, record where it is, which heuristic it breaks, and a severity:

- 0 not a problem · 1 cosmetic · 2 minor, low priority · 3 major, fix before release · 4 blocker, people can't complete the task

The ten: visibility of system status; match with the real world; user control and freedom; consistency and standards; error prevention; recognition over recall; flexibility and efficiency; minimal design; help recognising and recovering from errors; help and documentation.

**Target:** no severity 3 or 4 issues; a falling count of severity 2 from one version to the next.

A cognitive walkthrough complements this. For each step of a top task ask: will the person know what to do next, will they see the control, will they connect it with their goal, and will they understand the feedback? Any "no" is a finding.

### Tier 3: measured with people or usage data

| Measure | How | Target |
|---|---|---|
| Task success rate | share of attempts completed without help | 90% or more on top tasks (about 78% is a typical average) |
| Time on task | median seconds for a top task | lower than the previous version; for experts, close to the minimum the steps allow |
| Error rate | slips and mistakes per task; share of form submits that fail validation | falling; under 10% of submits |
| Single Ease Question | "How easy was this?" on a 1 to 7 scale, right after the task | 5.5 or higher |
| System Usability Scale | 10-item questionnaire, score 0 to 100 | above 68 is above average; 80 or more is good |
| First-click success | did the first click go the right way | 80% or more; a right first click strongly predicts finishing the task |
| Form completion | share who start a form and finish it | rising; note the field where people leave |
| Findability | share who locate an item from the navigation alone | 80% or more |
| Adoption and return use | share of intended users using the feature, and using it again | rising |
| Support load | questions or tickets about the feature per active user | falling |

### Reporting

When you finish designing, building or reviewing an interface, close with a short scorecard so the result can be compared with the next version. Size it to the job: a small change needs three to five lines, covering the measures the change was meant to move and any it could have harmed. Leave out measures that have nothing to do with the work. A figure that didn't change is reported as unchanged, never dressed as an improvement, and a line you couldn't measure says "not measured" instead of a guess. The full shape, for a new screen or a review:

```markdown
## Interface scorecard: <name of the view or flow>

**Users and top tasks:** <one line>

| Measure | Before | After | Target | Met |
|---|---|---|---|---|
| Steps for <top task> | 9 | 4 | fewer | yes |
| Lowest text contrast | 3.2:1 | 7.1:1 | 4.5:1 | yes |
| Smallest target | 18 px | 44 px | 24 px | yes |
| Keyboard-only completion | no | yes | yes | yes |
| No sideways scroll at 320 px | no | yes | yes | yes |
| States designed | 3 of 8 | 8 of 8 | 8 of 8 | yes |
| Heuristic issues (sev 3–4 / sev 2) | 2 / 6 | 0 / 1 | 0 / falling | yes |
| Tier 3 measures | not measured | not measured | | |

**Open issues:** <severity, location, heuristic, suggested fix>
**Checked by:** <what you actually did: screenshots at which widths, themes, keyboard pass, tools run>
```

## Quick review checklist

Use this for a fast pass on any screen.

1. Can I say in one sentence who this is for and what they do here?
2. Is there exactly one obvious primary action?
3. Would a newcomer understand every label without knowing the data model?
4. Does it follow the patterns of the screens around it?
5. Are empty, loading, error and success states all handled, with typed work preserved on error?
6. Can the top task be done with the keyboard alone, with focus always visible?
7. Does it work at phone width, at 200% zoom, in dark mode, and with long or missing data?
8. Is every destructive action either undoable or confirmed with its consequence named?
9. Is anything asked of the person that the software could work out?
10. Is everything in the change something that was asked for, with the rest listed as recommendations?
11. Have I filled in the scorecard with real numbers?
