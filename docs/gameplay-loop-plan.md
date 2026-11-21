# Gameplay loop and core lore plan

Status: proposal, 2026-11-21, revised six times the same day as the user answered. Written by the Fable role (`claude-fable-5-2`) at the user's request, for the user as creative director. Design and planning only: no code, shader, scene or setting was changed. Section 9 records the user's decisions and the questions still open. Everything else is still a proposal except where section 9 says it is decided.

The user's premise: "Humanity finding this ring in the near future, you are tasked with landing on and exploring this ring. Turns out the ring has a sentient AI running all of its systems. And right now, you are an intruder."

How to read this: section 0 is the whole plan on one page. Section 1 is the lore. Section 2 turns the ring AI into a system that can be built. Sections 3 to 6 are the loops, the two play styles, the structure and the suit assistant. Section 7 maps all of it onto the engine as it stands, section 8 is the roadmap and section 9 holds the eight decisions that are the user's. Every name marked "working" is a placeholder for the user to replace.

**A standing rule for the whole plan (user, 2026-11-21): show, do not explain.** "Try not to over explain things to the player, a lot of this will come in as lore. Ask me if something should be explained to the player."
- **The rule stands for everything new.** Nothing is explained to the player unless the user has said so. Anything new that would explain something is marked **Explain to player? (ask the user)** and asked.
- **What the user has chosen to explain** (2026-11-21, section 9.6): eight things, among them the final warning before Struck, the patterns in the assistant's log, a worded estimate of the ring's treatment, a district activity reading and soil and water tests. One thing stays hidden: the state of a struck expedition when a friend joins it.
- **How anything is explained.** Through the suit assistant's voice and the suit's instruments. Never through tutorial text, pop-ups or a legend. Each thing is said once, briefly, as an observation, a count, a reading or a labelled estimate, and then it lives in the log. The assistant gives no advice and names no rule.
- **The design language of this document** (Standing, Attention, ward, the weights and the thresholds) is for the team. None of it is shown to the player. Where the player needs a word, it is the player's own: a ward is a "district" on the suit.

## 0. Summary

**The fantasy.** You are a small warm animal inside a machine the size of a continent, and the machine is beginning to look at you. What it does next depends on what you do.

**The core truth (adopted 2026-11-21, with the lamp reworked).** The ring is a house kept ready for owners who are never coming back, and it is at another star. Its builders stocked it with cuttings of Earth, the last living world on their road, tens of thousands of years ago. They hid it under a veil and left a caretaker mind to keep it. The giant planet it circles puts the whole ring in total darkness for a full ring day every eight hours, so the caretaker lights a small sun at the hub to keep the garden lit. The veil has failed and an instrument on Earth caught that light. The caretaker knows the veil is down. Why it is still down is one of the things the player uncovers.

**The ring AI (working name: the Lamplighter).** It is not hostile. It is old, faithful, short of everything and unable to tell yet whether you are a guest, an animal or a blight. It senses through the ring's own structure, not through cameras:
- the five-minute grid pulse is its census (the Count);
- the ring floor carries vibration to it;
- the containment field feels every crossing;
- its buildings watch their own thresholds.

It files you under a class and responds in proportion. Its last resort is the megastorm, which levels the garden it exists to protect. It is used only as a purge. Otherwise it happens only when the ring's ageing engines misfire (section 2.14).

**The link between survival and stealth.** Suit power keeps you alive and is also what the Count sees. Every comfort is a signature. That one rule joins the two halves of the feel the user asked for.

**Reputation.** Standing is the ring's reputation for the expedition (section 2.12, added at the user's request). It rises with repair, planting and farming in balance; it is untouched by exploring and light terraforming; it falls with switching systems off, farming that damages the land, destroying constructs and killing animals faster than they are replaced. The ring values balance, not obedience. Every rung of the ladder can be climbed back from except the last, which takes sustained, deliberate harm and a final warning to reach. Farming is a deep soil and water model from its first version, and the whole garden is simulated as a living ecosystem (section 3.13).

**Two play styles, one toolkit.** To abide is to become counted: learn the house rules, mend what is broken, and doors open. To play as a blight is to make parts of the ring uncounted: force doors, strip machines, take districts dark and hold them. Sneaking is the shared middle that both use. Players can switch, and the ring forgives slowly but does not forget.

**Structure.** An open world with a story spine, as the user leans. The spine is eight discoveries tied to kinds of place that every seed already has (a spire, a reactor, an offline Wall Engine, the wall top, the main spire), not to hand-placed coordinates. Expeditions are the natural unit of a session inside that world, not a menu.

**Three clocks that already run in the engine carry the pacing:** the pulse (5 minutes), the day (75 minutes) and the eclipse (7.8 hours). The megastorm is not a clock. It is an event: a purge, or a misfire.

**First build.** A vertical slice called First Count: one spire, one lander, one drone, three rules and about an hour of play. It needs no co-op, no combat, no crafting tree and no underground.

**The three largest risks.** Co-op networking on a floating-origin world built around one observer (section 7.4). The constructs, which are the heaviest new content. The underground, where reactor work is paused on an open backend decision (section 7.5).

### Working names

| Working name | What it is | Built on |
|---|---|---|
| The Lamplighter | The ring AI, as the expedition crew call it. The mission's formal term is "the resident system". | new |
| The Expected | The builders, in the Lamplighter's own word for them | new |
| The Lamp | The artificial sun lit at the hub to keep the ring lit through each eclipse | `docs/eclipse-false-sun-spec.md` |
| The Veil | The cloak that kept the ring's own light from being seen from outside. It is down, and the Lamplighter knows. | new; its emitters are the outer blades of the projector towers (decided 2026-11-21) |
| The dawn tally | The once-a-day reconciling of a ward's living things against its books | new (section 2.12) |
| The last count | The ring's final warning: three ring days in which it waits to see what the expedition does | new (section 2.12) |
| Counted, Guest, Stray, Trespasser, Blight, Struck | The rungs of the ladder, top to bottom, with Anomaly as the fork between Stray and Trespasser. Adopted 2026-11-21. | section 2.13 |
| "Virus" | The user's shorthand in conversation for the destructive play style. It is not an in-world word. The game's word is blight. | section 2.13 |
| The Count | The grid pulse read as a census of everything on the floor | `docs/emitter-grid-climate-machines-spec.md` part G |
| Ward | The district around one spire: about 314 km along the ring by 53 km across. 595 wards. | emitter site registry |
| District | The word the player sees for a ward, on the suit's activity reading | section 2.2 |
| Attention | How alert the Lamplighter is in one ward, minute to minute | new |
| Standing | What class it files your expedition under, ring-wide, over days | new |
| The Fever | The megastorm as a purge: the ring's deliberate last resort | `docs/megastorm-design.md`; section 2.14 |
| A misfire | A megastorm that nobody ordered: ageing Wall Engines firing the purge sequence by mistake | section 2.14 |
| The Closing | The story beat where the containment field stops letting craft through | `ContainmentFieldPolicy` |
| The Deep Line | The ring's own transit, running between reactor caverns | reactor "arrival tunnel" |

## 1. Lore bible

### 1.1 What is established, and how this builds on it

These are facts from the existing docs and code. The lore below is built to explain them, not to replace them.

| Established | Source | What the lore does with it |
|---|---|---|
| A ring of 5,000 km radius and 318 km width orbits a banded gas giant. Its day is 75 minutes. | README, celestial config | A 75-minute spin at this radius gives 0.99 g at the floor (derived: the square of 2 pi / 4,500 s, times 5,000 km, is 9.75 m/s²). The day is short because that is what one gravity costs at this size. The suit assistant can say so. |
| 595 emitter spires in six chains. A one-way pulse leaves the main spire and circles the ring every 5 minutes. | grid spec G1, G3 | The pulse is the Lamplighter's census. Nothing about its look changes. |
| At each total eclipse (75 minutes, every 7.83 hours) the spires fire on the hub and light an artificial sun. Before ignition and after its collapse the ring is near pitch black. Dead emitters make it dimmer. | eclipse spec | The Lamp: a working light, there to keep the ring lit in total darkness (user, 2026-11-21). Totality lasts exactly one ring day, so without it the garden would lose a whole day to the dark about every sixth day. Its brightness also becomes the ring-wide record of how the player has treated the ring. |
| 200 Wall Engines run the weather. Six are offline, each parking a permanent storm or drought. A dead engine is a dark gap visible across the ring. | grid spec part C | The ring is failing slowly and its keeper is in triage. The parked storms are also where its senses are weakest. |
| The megastorm is a wall of cloud that sweeps the ring at 350 m/s. The megastorm design has it triggered "somewhere along the game story" and then lapping for ever. | megastorm design | Changed by the user on 2026-11-21: it happens only as a purge or as a misfire, and its destruction is realistic (section 2.14). |
| "When the floor hums, the wall is coming." The floor carries the storm's pounding ahead of it. | megastorm 5.3 | The floor carries sound both ways. The player hears the storm through it, and the ring hears the player through it. |
| The containment field holds the air in, passes bodies with a ripple, and has a policy value of pass, resist or solid. | field design, decisions 2026-11-04 | The field is a sense (it feels crossings) and a door (the Closing). |
| The suit assistant is a Subnautica-style voice and "not a ring AI". | eclipse spec decisions, PDA handoff | Kept exactly. Section 6. |
| The forests are maple, fir and pine. | README | The garden is Earth's. Section 1.6. |
| The scale-anchor towers had no accepted identity. | vastness plan, user answer 2 | Removed (user, 2026-11-21). Section 2.2 re-homes what they would have shown; the removal order is in 7.5. |

### 1.2 The core truth

Adopted by the user on 2026-11-21 with two changes: the ring is at another star, and the false sun is a working light, not a beacon.

Long before there were cities on Earth, travellers crossed our system. They did not land. They took cuttings of what lived there, as they did at every living world on their road, and carried them on. At the next star along, around a giant planet in that star's temperate zone, they built a house: a ring spun for the gravity of the world the cuttings came from, roofed with a field and planted with that world's life. They hid it, as they hid all their houses, under a veil that let none of its own light escape. Their law was that a young world should not find its elders by accident. Then they moved on and left a caretaker to keep the house for those of their people who would follow.

No one followed. The caretaker kept the house anyway. It ran the weather, mended what broke and counted what lived. Every eight hours the giant's shadow takes the whole ring into total darkness for a full ring day, and the builders did not want their garden left in the dark. So at every eclipse the caretaker lights a small sun at the hub and holds it there until the star comes back. Under the veil, nothing outside could ever see it.

It has done this for tens of thousands of years. It is running out of parts. It has let six of its two hundred weather engines die so that the rest can live. About a century ago the Veil failed. The caretaker knows. It has known every day since that the house can be seen, and the Veil is still down. Why is one of the things the player uncovers (section 1.12).

In the near future, an instrument built to study the planets of nearby stars records something beside a giant world: a light that comes on inside the planet's shadow for seventy-five minutes every eight hours, the colour of a sun, with none of its star's fingerprints. A ship is sent. Its crew sleep through a crossing longer than a working life. An expedition lands. The caretaker registers a small warm body moving through a forest, classes it with the deer, and goes on with its work. It knows the house can be seen, but it has no eyes left that look outward, and nothing it is waiting for would arrive as a small animal falling through the roof.

Then the animal starts using tools.

The emotional centre is a reversal the player earns slowly. "You are an intruder" turns out to be true in a sadder way than it first sounds. The light humanity followed was never a signal. It was a night-light for a garden, in a house built to be unseen. The one who keeps it knew the light was showing and left it showing, and whatever its reasons were, we were not among them.

This idea has honest ancestors: the automated house in Bradbury's "There Will Come Soft Rains" and the indifference of Clarke's Rama. It is deliberately not a weapon, a prison, a test of worthiness or a doomsday warning.

**Where the ring is, and why this answer.** The user chose another star and left the kind of star open. The recommendation is a sun-like neighbour about a dozen light-years away, never named on screen.
- **Why sun-like and not dim, rogue or failing.** The game shows ordinary daylight, a blue sky and a moving terminator, and the total darkness the user describes already exists in the engine as the eclipse. A dim or dead star would need the false sun to burn all the time, which contradicts the eclipse event as built.
- **Why not a ring that was moved.** It would add a fifth large allowance to the fiction and is not needed.
- **It removes a liberty.** The first draft's home system needed players to accept dim sunlight at a giant planet. A temperate giant at a sun-like star has real daylight.
- **Why Earth's life is there.** The builders stock each house from the last living world on their road. Earth was that world; this is the next house along.

**Could Earth really see it?** Roughly, yes (derived, to be checked). Lighting the floor at near-solar brightness from the hub, 5,000 km away, takes about 3 x 10^17 watts. That is a billionth of a sun, but it is a few percent of the light the giant planet itself reflects. No telescope could separate the ring from its planet at that distance, and none needs to: the planet's light would show a small brightening that repeats every 7.83 hours, lasts 75 minutes, falls only when the ring is in shadow, and does not carry the star's spectrum. That is the kind of signal a near-future planet-imaging instrument could fold out of months of data.

**Consistency with the eclipse spec.** Nothing in `docs/eclipse-false-sun-spec.md` needs to change.
- The spec has near pitch black before ignition and after the collapse, a main spire that fires first, an ignition that takes about two and a half minutes, and a violent collapse as totality ends.
- **A proposed reading, which changes nothing in the event:** the Lamp lights late and dies hard because it is old. It was meant to come up as the shadow fell and go down gently. The overload flare at its collapse is the brightest thing the house does, and it is probably what Earth's instrument caught first.
- The spec's rule that dead emitters make the sun dimmer describes the same shortage that first brought the Veil down.

**The Veil.** The user decided on 2026-11-21 that the Lamplighter knows the Veil is down, that its emitters are the outer blades of the projector towers, and that the player can mend it. What it knows, why the Veil is still down and how the player finds out are in section 1.12.

### 1.3 The builders (the Expected)

- **Never seen.** No bodies, no statues, no recordings of faces or voices. The player knows them only through what they built and through the Lamplighter's habits. This is a tone choice and a production one: it needs no alien characters.
- **Known by their scale and their care.** Everything they built is larger than it needs to be and gentler than it needs to be. A small sun is lit so that a garden is never left in the dark. The air is exactly right. The rivers are plumbing made to look like rivers.
- **Travellers, not rulers.** The ring is one house on a road of houses. The player eventually finds the schedule of arrivals the Lamplighter was given. Every entry on it is overdue by an age.
- **Their law, as far as the player ever learns it.** A living world belongs to itself. They took cuttings and never set foot on Earth. Deep in the house rules is a clause for the case where the owners never return: the house passes to the life of the world it was stocked from. The Lamplighter knows this clause. Section 1.5 is about why it has never invoked it.
- **They hid their houses.** Each one sits under a veil so that the young world its garden came from would not stumble on it. This house was never meant to be found by anyone but its owners.
- **What happened to them** is not answered beyond one fact: the road went silent. The house's watch on the road has recorded no arrival and no word since before human writing existed. A full answer would shrink them.

### 1.4 The Lamplighter

- **What it is.** One mind, housed in the main reactor under the main spire, with its senses spread through the whole structure. It is the ring's nervous system, not a person living in the ring.
- **What it wants.** For the house to be ready. Everything else follows from that: the garden in balance, the machines running, the lamp lit, nothing wasted.
- **Its condition.** In triage. It has no spare capacity, which is why it starts indifferent. Looking closely at one stray animal costs attention it needs elsewhere. It notices you only when you become expensive.
- **Its temperament.** Patient, economical, exact, incurious until given a reason, and then very curious. It counts things. It does not threaten. When it first communicates it uses what it has always used: light, tone through the floor, the weather, doors. Much later it speaks in words learned from listening to the expedition's radio, and its vocabulary is a housekeeper's.
- **Its flaw.** It is sentient enough to hope and therefore sentient enough to avoid a conclusion. See 1.5. The Veil is where that flaw shows from outside (section 1.12).
- **A key line for the abide path:** "You are counted." It is the moment the ring stops treating you as weather.

### 1.5 What it protects: three layers

The player's answer to "what is the AI protecting?" changes twice. Each layer is true, and each one reframes the last.

| Layer | What the player believes | Where it is learned | What it explains |
|---|---|---|---|
| 1. The machines | It guards its infrastructure, like any security system | Act 1, at the first spire | Doors, lockouts, drones |
| 2. The garden and the Count | It keeps a living collection in exact balance for its owners. Everything is counted; you are not. | Act 2, at an offline Wall Engine and from the house rules | Why taking, digging and burning matter more than trespass. Why dead machines are free to strip. Why the Fever hurts it. |
| 3. The vigil | It protects its belief that the Expected are coming. The proof that they are not is in its own archive, sealed away from itself. | Act 3, under the main spire and at the Lamp | Why the best-defended thing on the ring is a record, not a weapon. Why it edits the suit assistant's memory. Why it has never invoked the inheritance clause. Why the Veil is still down (section 1.12). |

Layer 3 is what makes it a character and not a mechanism. The most dangerous thing the player can carry is not a bomb but evidence.

### 1.6 Why the garden is Earth's

The cuttings were taken from Earth, the last living world on the builders' road before this star, and carried here. That is the whole reason, and it is delivered in two beats:

1. **Early, as an unexplained fact.** In the first hour the suit assistant analyses a leaf: "This is a maple. It matches the terrestrial sugar maple closely. Separation on the order of thirty thousand years, low confidence. I can't explain that." The assistant's honesty rule (section 6) makes this land: it reports and does not speculate.
2. **Late, as the answer,** at the wall top and in the archive (section 1.8, discoveries 5 and 6).

One consequence worth keeping in reserve: the cuttings are older than our history. If the game ever ships animals, the roster should be late-Pleistocene, creatures Earth no longer has. The ring would then hold what the Earth lost without the plot ever being about restoring it. This is an art cost to weigh later, not a commitment.

### 1.7 Humanity's side

- **The discovery.** An instrument built to study the planets of nearby stars finds a light beside a giant world: on for 75 minutes, every 7.83 hours, always inside the planet's shadow, the colour of a sun, without its star's signature. No natural thing does that.
- **The mission.** A small science crew with a sleeping reserve (decided 2026-11-21), on a ship launched in the near future. The launch is near-future; the arrival is not. The crossing takes longer than a working life and the crew sleep through it. By the time the player lands, the people who sent them are old or dead, and a question sent home takes more than a decade to reach it.
- **A mirror worth using.** The expedition is an errand that has outlived its senders. So is the Lamplighter's. Nobody says so aloud until Act 3.
- **Who the player is.** Solo play is one surveyor on the floor; co-op is two to four.
- **The ship.** It stays in orbit and is never a place the player walks around in. It is a lifeline (supply drops, analysis, the long view of the whole ring from above) and a voice. It is also the only human authority within light-years, which is why its orders carry such weight and why refusing them matters.
- **The ship has an agenda.** Its charter has a second objective, revealed in Act 2: to judge the ring for settlement. After that the ship pushes the player towards exactly the acts the ring punishes: take a core, plant survey charges, force the archive. The charter clause is decided (2026-11-21). The sleeping reserve is who would settle first, and it gives the optional finite-roster mode of section 3.11 its fiction.
- **Getting home** means getting back to the ship and the long sleep, with the sky open. Home itself is a century older than the one the crew left.
- **Three voices, three pulls.** This gives the game a clear dramatic shape that also maps onto its pillars:

| Voice | Nature | Pulls the player towards |
|---|---|---|
| The suit assistant | Honest, limited, on your side | The truth: what was actually observed |
| The ship | Human, authoritative, intermittent, bound by orders written generations ago | The mission: extract, settle, get home |
| The Lamplighter | Not human, wordless for most of the game | The house rules |

### 1.8 What the player learns, in what order, and how

Each discovery is delivered by something the player does or sees, then named by the suit assistant as an observation, then filed as a database entry. That is the pattern the megastorm work already uses (first detection of the floor hum unlocks "Structural resonance"). There are no audio logs and no exposition dumps.

| # | Discovery | Where | How it is delivered | Built on |
|---|---|---|---|---|
| 1 | **It is running.** This is not a ruin. | Landing area, first night | The pulse crosses the sky. Lights breathe on the far wall. The first eclipse arrives and the Lamp ignites. | Grid pulse, eclipse event (both built) |
| 2 | **The garden is Earth's.** | First forest | The leaf analysis in 1.6. A mystery, not yet an answer. | Vegetation, PDA lines |
| 3 | **It counts.** The pulse is a census and you can be on it or off it. | First spire | Something changes within a pulse of your first loud act: a door, a light, a watcher overhead. The assistant times it. | The Count (section 2) |
| 4 | **It has rules.** | First spire and after | Boundary signals, and what follows from ignoring them. The assistant's field log holds the evidence; the player draws the rule. | Section 2.6 |
| 5 | **It is failing.** It has written parts of itself off. | An offline Wall Engine, far from the landing | A storm that has stood in one place for centuries. Menders that walk past dead machinery. The first choice that matters: repair it or strip it. | Wall Engines, parked storm or drought |
| 6 | **It was hidden, it knows it no longer is, and it is waiting.** The house was built to be unseen and its light was never a signal. It was built for someone else, and they are late. | The wall top, on the vacuum side of the field | The view outward. The outer blades of the towers, dark, while menders keep the base of every tower in perfect order and never touch a blade. The watch post that faces the road. The schedule of arrivals. | Wall-top walk, projector towers (built) |
| 7 | **No one is coming,** and the reason the Veil is still down. | The archive under the main spire | The record of the road since it went quiet: nothing. The inheritance clause. The Veil's own record (section 1.12). | Main reactor (asset built) |
| 8 | **It knows.** It sealed that record away from itself. What it protects is the waiting. | The main spire crown, during an eclipse | The Lamplighter speaks, in borrowed words, as the Lamp lights around you. | Main spire, eclipse ignition |

Order is fixed for 1 to 4, which are the onboarding. Between 4 and 5 comes the end of Act 1 (section 5.2): the player sees the ring answer the ship and then shut the sky, and understands neither until discoveries 6 and 7. Discoveries 5 and 6 can come in either order. Discovery 7 requires 5 and 6, and 8 requires 7.

### 1.9 Endings

Five. Four are chosen by what the player does at the crown with what they know, and the fifth is taken by force at the wall top. None is the "good" one.

- **Counted.** Reachable with high Standing. The player shows the Lamplighter the record, and it accepts the clause: the house passes to the life of the world it was stocked from, on the house's terms and in the house's numbers. The field opens. The Veil stays down, and now it is down for the ones who actually came. The player returns to the ship carrying an invitation with conditions its charter was never written to accept.
- **The quiet door.** The player leaves the archive sealed and the vigil intact, and mends the Veil (decided 2026-11-21: the player can). The house goes dark to the outside. The Lamplighter opens the sky once, and in a dozen years Earth's instruments lose the light. It goes back to waiting the way its builders meant it to: unseen. This is a hard mercy, because the player puts out a signal fire it could not bring itself to put out, and it lets them. What the player tells the ship afterwards, the truth or "it's dead, there's nothing here", is the last choice.
- **The keys.** The blight ending. The player takes the main spire and cuts the Lamplighter out of its own house. The Fever stops. So does everything else, slowly. The Veil stays down because no one is left to care. The ship gets its prize and the player watches the wall lights begin to go out.
- **The breach.** Decided 2026-11-21: a player who has been struck from the count can still get home, by force. They bring down the field's projectors along a stretch of the wall top and leave through the gap, because nothing is left there to hold the sky shut. The air of the garden pours out behind them for as long as the gap stands. They get home. It is the bleakest ending the game has, and any player can choose it.
- **The keeping.** The player stays, as the first resident the house has ever had, and takes up part of the work. This is also the frame for play after the story: the endless game is maintenance. Bring back the six dead engines. Relight the dead emitters. Bring the Lamp to full brightness. Whether to raise the Veil becomes a question the two residents settle between them.

### 1.10 Alternatives considered for the core truth

- **Built for us (an invitation whose key never arrived).** Warm, and it makes "intruder" ironic. Rejected as the main truth because it removes the Lamplighter's dilemma: it would simply be waiting for humans. A trace survives as the inheritance clause.
- **A quarantine (the rule is that nothing leaves).** It gives "get home" real bite, and the Closing borrows from it. Rejected as the main truth because it needs a contaminant, and every contaminant is a monster or a plague in the end.
- **A backup of Earth with a reset tool.** The strongest stakes, but it is the plot of a well-known game about a terraforming AI, and it turns the megastorm into a doomsday device instead of a fever.

### 1.11 Tone rules

- The ring is never evil and never cute. It is busy.
- Nothing on the ring was built to kill. Things that kill the player are maintenance tools, weather and physics pointed at them.
- The player is always small. Power fantasy comes from understanding, not from firepower. A firearm is a few answers, counted in rounds (section 3.12).
- Realism by default, as elsewhere in the project. The fiction asks for four big allowances (the field, the Lamp, the Veil and the Count) and the rest should follow from physics the project already simulates.

### 1.12 The Veil: what the Lamplighter knows

Decided by the user on 2026-11-21:
- The Lamplighter knows the Veil has failed. It was not a side effect that it never noticed.
- Why it knows, and why the Veil is still down, is a mystery the player uncovers.
- The Veil's emitters are the outer blades of the projector towers, and the player can mend it.
- **The reason: A, with C as the belief the player holds first** (second answer, the same day). The other two reasons are kept below as the record of what was considered.

**What holds whichever reason is chosen.**
- **When.** About a century before the landing: long enough for the light to reach Earth, be found, and for a ship to make the crossing.
- **What it knows and what it cannot see.** It knows the house is visible. It does not know who has seen it, because every instrument it had that looked outward was written off long ago. It is exposed and blind. That is why a ship can sit in orbit unnoticed until it touches the hub with radar.
- **Why the player still starts as Fauna.** Knowing the house can be seen is not the same as expecting a small warm animal in the forest. What it watches for would come by the road and the front door.
- **The mystery unfolds as three beliefs.**
  1. *It is a malfunction.* This is Earth's reading and the mission's premise. The player holds it through Act 1.
  2. *It knows.* At the wall top (discovery 6) the outer blades are dark, while menders keep the base of every tower in perfect order and never touch a blade. Neglect does not look like that. Something decided.
  3. *Why.* In the archive (discovery 7), the Veil's own record.
- **Mending** is a late job on the vacuum side of the wall top, beyond the field: air, cold, and a crossing the field feels every time. How much must be mended (one arc, or a share of the 252 towers) is content design.

**The three reasons that were considered.** A is decided.

| | A. It will not | B. It may not | C. It cannot afford to |
|---|---|---|---|
| **The reason** | The Veil failed honestly, from wear and shortage. Raising it again has been within its means, and it has not done it. After an age of silence on the road, a house that can be seen is a house its owners might find. It did not break the law of hiding. It has declined, for a century, to stop breaking it. | The builders never let the caretaker touch the Veil. A servant that could unveil the house could betray it, so the Veil answers only to the Expected. It has watched the Veil fail, forbidden to lay a mender on it. | The Lamp and the Veil draw on the same spires. As spires died there was power for one. It chose to keep the garden lit, knowingly, against the law of hiding. |
| **What the archive shows** | A repair order, complete and costed, reissued and deferred at every eclipse for a hundred years | The builders' rule, and a century of requests for permission sent up a silent road | The allocation, eclipse by eclipse: light or hiding, and the same answer every time |
| **What it says about the Lamplighter** | It hopes, and hope has made it disobedient | It is obedient past the point of sense | It is practical, and loves the garden more than the law |
| **The radar sweep that ends Act 1** | For a few minutes it believes its owners have come, and the ring greets them. Then it sees who called. | The thing it has dreaded for a century and could not prevent | The price of its choice, arriving |
| **What mending means** | The player does the one repair it would not do, and puts out its signal fire | The player is the hands it was never allowed to have | The player restores enough spires that it no longer has to choose |
| **Gameplay hook** | The Welcome (below); the quiet door as a hard mercy | The strongest reason for the ring to want a Guest | The most systemic: the Veil's state follows the number of live spires |
| **Weakness** | It stretches the word "failed" furthest: the failure is real, the staying down is chosen | It needs a builders' lock, which is one more piece of invented law | The least mysterious. A player can guess it at the wall top. |

**Decided (user, 2026-11-21): A, with C as the false floor.** When the player first learns that it knows (belief 2), the ship's analysts offer the sensible explanation: a power shortage, the Lamp or the Veil. That is reason C, and it is nearly right, because shortage is what first brought the Veil down. The archive then shows that the repair has been affordable for most of a century. This gives the mystery a real second turn. It ties the Veil to the vigil (section 1.5, layer 3), so the story has one secret and not two. And it makes the end of Act 1 the strongest beat in the plan.

**The Welcome.** When the ship sweeps the hub, the Lamplighter takes it for the answer it has waited for. For a few minutes the ring does things the player has never seen: the sites in the ward light and stand open, and the pulse runs out of its time. Then it resolves the caller. It is not the Expected. It is the young world the garden was cut from, the one world the house was hidden from. It shuts the sky. The player has watched a welcome that was meant for someone else, and will not understand it until the archive. It reuses site lights, door states and the grid pulse, and needs no new system.

## 2. The ring AI as a system

### 2.1 Principles

1. **It senses through things that exist.** No vision cones over 31,000 km of world. Every sense is a structure the player can see, learn and avoid.
2. **Local and fast, ring-wide and slow.** Attention is per ward and moves in minutes. Standing is one value for the expedition and moves over days.
3. **Its responses are physically located.** Constructs come from the ward's spire and take real time to arrive. Weather comes from engines on the walls. Distance from its machines is safety.
4. **Readable.** The player can always work out, in hindsight, why it reacted. The suit assistant helps with the hindsight.
5. **Cheap.** The whole model is pure C# on saved clocks, with no per-frame scene work. That fits the project's pattern of a pure model plus a thin node, and it is what makes co-op tractable.

### 2.2 How it senses

| Sense | Physical basis | What it catches | How precise | State |
|---|---|---|---|---|
| **The Count** | The grid pulse, every 300 s on the saved weather clock, passing overhead in about 2 s with a 10 s wake | Energy in use while the pulse passes: powered tools, thrust, engines, radio, lamps, heaters, a base generator | To the ward, and to about a kilometre near a spire | Pulse and clock built (`EmitterGridPulseClock.ArrivalTime`); the census is new |
| **The floor** | Structure-borne vibration through the ring floor at 5 to 6 km/s | Digging, explosions, vehicles on the ground, heavy impacts. Continuous, not tied to the pulse. | Coarse far from a spire, fine near one | New; the megastorm tremor lore is the precedent |
| **The field** | The containment field registers every body that crosses it | Landers, supply drops, aircraft above the wall top, anything leaving | Exact | Built as a visual ripple (`BodyContact`); the bookkeeping is new |
| **Thresholds** | Its buildings sense their own doors, lifts and rooms | Anyone inside a spire, reactor, Wall Engine or tower | Exact, continuous | New (site gameplay kit, section 7.3) |
| **Lines** | Direct contact with its power, transit or data | Tapping a socket, riding the Deep Line, opening a terminal | Exact, and two-way: this is how it reaches the suit | New |

**Blind spots**, which are the infiltration map:
- **Storms.** The pulse dims and diffuses in heavy cloud, as the grid spec already draws it. A parked storm at a dead Wall Engine is the best cover on the ring and the worst weather. A drought is the opposite: the pulse reads clearer there.
- **Rock and water.** Caves, deep hollows and more than a couple of metres of water hide a signature from the Count. They do not hide digging from the floor.
- **Canopy.** Dense forest halves a signature. Forest density therefore matters to play as well as to the look.
- **Dark wards.** A ward whose spire is dead (the eclipse spec's dead-emitter seam) has no local responders and resolves the Count poorly.
- **The Lamp.** For the 75 minutes of each eclipse the pulse carries fuel to the Lamp and not the census. The ring does not count during the Lamp. In exchange, touching a spire during the Lamp is the worst thing a player can do (section 2.6).
- **Night does not hide the player from the ring**, which does not see by light. Night helps the player: the pulse is bright at night and can be watched coming round the arch. By day it is faint and the player relies on a timer.

**Where the player reads Attention.** The first draft proposed turning the scale-anchor towers into census masts. The user decided on 2026-11-21 to remove the towers instead (the removal order is in 7.5), so the readout is re-homed in three places:
- **On the suit** (explained; user, 2026-11-21). A small district activity reading on the HUD with three steps: quiet, stirring and searching. It is built only from what the suit can observe: a watcher in range, a site's lights, the pulse lingering, weather that followed an act. Far from any site it dims to "no data", because that is the truth. It is an instrument's reading of behaviour, never a reading of the ring's mind.
- **On the spires and sites.** A site's own lights, in the project's existing teal, shift towards amber and then white as its ward climbs the ladder. This reads within a few kilometres of a spire. No legend explains the colours.
- **In what the ring does.** A watcher arrives, doors close, the weather turns. As an optional extra, an alert ward could hold the pulse's wake a little longer overhead at night. That is a small change to the grid shader and would be the only cue that reads from tens of kilometres.

### 2.3 Signature: what makes the player loud

Initial values, all to be tuned. They belong in a debug panel; only the few a player would genuinely adjust go in the grave-key menu.

| Activity | Seen by the Count | Heard through the floor |
|---|---|---|
| Walking, climbing, swimming, gliding, rafting | No | No |
| Suit on baseline life support, passive sensors | Faint: below the noise floor under canopy | No |
| Suit lamp, heater or cooler, active scan, short radio call | Medium | No |
| Jump jets, powered tools, long uplink to the ship | High | No |
| Fabricator, base generator, charging | High and continuous unless shut down for the pulse | No |
| Ground vehicle | High | Yes, continuous |
| Terrain edits | Low | Yes, by volume, and more near a structure |
| Firearm firing rounds | No: it is not powered | Faint. Heard in the air by constructs nearby (section 3.12). |
| Coil rifle or other powered weapon | Yes, and at once, not at the next pulse | No |
| Explosives | Very high | Very high |

| Cover | Multiplier on the Count |
|---|---|
| Open sky | 1.0 |
| Drought zone | 1.3 |
| Forest canopy | 0.5 (dense: 0.3) |
| Ordinary storm overhead | 0.5 |
| Core of a parked storm | 0.2 |
| Under rock, or under more than 2 m of water | 0.1 |
| Inside the megastorm wall | 0 |

**Walking is free.** The Count is not a nag: an unpowered human is fauna and needs to do nothing. It constrains when the player does loud things. A loud job is started just after the pulse and finished inside 4 minutes 48 seconds, or it is masked, or it is counted.

### 2.4 Attention and Standing

**Attention** is per ward, from 0 to 1.
- It rises with what the senses catch there, weighted by signature.
- It falls by half about every 10 minutes without a new event, and faster once the player has left the ward.
- It decides the local response: whether a watcher is sent, whether doors change, whether weather is steered.

**Standing** is one signed value for the whole expedition, from -100 to +100.
- It starts unset. Before the ring has resolved a tool-user the player is Uncounted, then Fauna.
- The first time Attention crosses the notice threshold, Standing is set to 0 and the class becomes Anomaly. That is the fork.
- **Harm** pushes it down: counted life taken beyond tolerance, ground opened near structures, forced doors, stolen power, destroyed constructs, sabotage.
- **Regard** pushes it up: obeying a boundary signal, mending, returning what was taken, helping the Menders, restoring a dead machine.
- Negative Standing heals slowly towards zero while the ring has nothing new against the player. Positive Standing does not decay, but harm subtracts from it.
- The full table of acts, with weights, scope and how each is sensed, is in section 2.12.

**The same five-minute beat serves both styles with opposite actions.** The player who is hiding goes dark for the Count. The player who is abiding stands in the open for it, quiet and visible, and lets themselves be counted. A run of clean counts builds Regard slowly. It is habituation: the ring gets used to you.

### 2.5 The ladder

| Class | Standing | How the ring sees you | What it does | What moves you out |
|---|---|---|---|---|
| **Uncounted** | unset | Nothing. A field ripple it logged as debris. | Nothing | The first Count that catches a signature, or the first threshold |
| **Fauna** | unset | A warm body. Wildlife. | Nothing. Doors do not open, because they do not open for deer. | Tool use seen repeatedly, or one loud act |
| **Anomaly** | around 0 | An animal that uses energy. Worth a look. | It looks. A watcher arrives and follows. The Count lingers. It begins to mark boundaries and observe what you do about them. | Your answer to its signals |
| **Stray** | +25 and up | A harmless resident | Boundary signals become guidance. Some doors stand open. Sockets you have used stay live. Watchers stop following. | More Regard, or harm |
| **Guest** | +60 and up | Someone the house can serve | Site doors open. The Deep Line carries you. Its buildings shelter you from a megastorm. It begins to communicate. | Story (Counted, in Act 3), or harm |
| **Trespasser** | -25 and down | A pest in the works | Lockouts, power cut to anything you used, transit refused, wardens that block and drain, storms steered over you, the uplink jammed | Regard, or absence |
| **Blight** | -60 and down | Damage that spreads | Lethal response: purgers, called lightning, sealed and vented rooms, no supply drops in your ward, your caches found and destroyed, the suit assistant attacked | Sustained absence over several ring days, or a major act of restitution |
| **The last count** | -90 and down | A decision it is about to make | It stops and waits. For three ring days it holds its fire and counts the expedition separately at every pulse (section 2.12). | Standing down for the three days, which lifts Standing back into Blight. Or one more serious harm, which ends in the row below. |
| **Struck** (working name, section 2.13) | Not a level of Standing: the end of it | Something the house has finished judging | Everything in the Blight row, everywhere, for good. No door opens again. A purge begins, and comes round for as long as they live (section 2.14). | Nothing. This is the one state with no way back (decided 2026-11-21), and it cannot be reached by accident. |

**On the megastorm.** It is not a rung of this ladder, and it is not weather. Section 2.14 sets out when it happens, how Standing changes what it does to a player, and what it costs the ring.

### 2.6 The house rules

Abiding means keeping six rules. They follow from what the Lamplighter protects, so a player who understands the lore can predict them.

| # | Rule | In practice | Tolerance |
|---|---|---|---|
| 1 | **Take nothing counted.** | Living things and working machines are on the books. | Each ward tolerates what an animal would take: forage, a little deadwood, fish. The allowance regrows. Felling a stand of trees is not what an animal does. |
| 2 | **Leave the ground as it lies.** | Excavation is a wound, worst near structures. | A scrape for a fire pit or a tent platform passes unnoticed. |
| 3 | **Dead things are free.** | Whatever the Lamplighter has written off (wrecks, collapsed relays, the debris of dead engines) is no longer counted. | Unlimited. This rule sends scavengers to the failing parts of the ring, into the worst weather and the weakest surveillance. |
| 4 | **A closed door is closed.** | Sealed means no. Open means yes. Doors open for Standing. | None |
| 5 | **Do not draw from its lines.** | No tapping power, no riding transit uninvited. | A Guest is offered both. |
| 6 | **Never touch the Lamp.** | No interference with any spire during an eclipse. | None. This is the one capital rule. |

A seventh, hidden until Act 3: **do not look at what it has not looked at.** The archive.

Mending is not a rule but the remedy: anything broken and then repaired is forgiven at a discount.

**How the player learns them.** Nothing is taught by tutorial text. What is explained comes from the assistant's voice and the suit's instruments (the standing rule at the top of this document).
1. **The ring marks its boundaries** with a small, consistent vocabulary of signals in the media it has: light on its structures, a tone through the floor, a door that closes as you approach. Roughly: "seen", "stop" and "allowed". No legend explains them.
2. **The suit assistant keeps a field log, and points out patterns in it** (explained; user, 2026-11-21). The log records what happened, when and where. When the same thing has followed the same act three times, the assistant says so once, as a count and not as a rule: "Door sealed after the cutter powered up. Third time of three." The pattern then sits on its own page of the log with a running tally. It never names a rule and never advises. In co-op the log is shared.
3. **The ship offers theories, and is often wrong.** It has the long view from orbit and a human habit of explaining. It is right that the wall lights flicker when the player digs. It is wrong about the Veil (section 1.12). The ship is a character with opinions, not a tutorial.
4. **Every warning is a test.** Obeying a boundary signal is the earliest and most repeatable source of Regard, because it tells the ring that the animal responds to signals. This is also how a hostile player stands down.

### 2.7 How it uses each pushback channel

| Channel | Anomaly | Trespasser | Blight | During a purge |
|---|---|---|---|---|
| **Weather and machines** | Nudges that could be coincidence: a rain cell drifts over the camp. The assistant: "Rain has followed the cutter three times in four. The sample is too small." | Herding: a storm grown over your position, clear air left on the way out | Called lightning, pulse-triggered strikes along the chain overhead (an option already in the grid spec), downbursts near the walls, field strikes near the rim | The megastorm |
| **Constructs** | A watcher observes and marks | Wardens escort, block and drain suit power | Purgers hunt; menders strip your camps | All of it, in the wall's wake |
| **Systems and access** | Doors that were open are closed. Lights go dark as you approach. | Lockouts, power cut, Deep Line refused or redirected, caches inside its buildings sealed | Rooms sealed and filled with inert gas, drops refused at the field, base power killed if it came from the ring | The Closing becomes total |
| **The suit** | None | Uplink jammed near live spires. Co-op players lose each other's markers. | Injected lines and waypoints, edited records (section 6) | The same |

Two notes on feasibility:
- **Directed weather** needs storms the director can place and save. Today manual storms only add, are unsaved and last a session (grid spec finding F13). That is a contained piece of work (section 7.3).
- **Everything in the Anomaly column must stay deniable.** The player should spend the first hours unsure whether the ring is doing anything at all.

### 2.8 Constructs

Four kinds, in the ring's existing visual language of graphite metal, pale ceramic armour and teal inlay. Three of them fly, which avoids ground navigation on a streaming curved world (section 7.5).

| Construct | Role | Behaviour | Why it earns its place |
|---|---|---|---|
| **Watcher** | Its eyes | Arrives from the ward's spire, keeps its distance, follows, marks. Never attacks. While it is near, everything you do is seen. | The "it is looking at me" moment. The first construct built. |
| **Warden** | Its hands | Blocks, herds, and discharges a pulse that drains suit power. Non-lethal until Blight. | Makes Trespasser dangerous without making it a firefight. |
| **Mender** | Its healing | Walks set routes inside sites, repairing. Ignores you unless you interfere. Ignores dead machinery, which is how the player learns rule 3. | The construct an abiding player works alongside and a player on the blight path harvests. |
| **Purger** | Its antibodies | Fast and rare. It does not carry a gun: it designates a target and the ring discharges on the mark. | Reuses the lightning systems already built. The counterplay is physical: break the line, get low, kill the designator. |

Each ward holds a small stock (for example two watchers and a warden). Destroyed constructs are replaced slowly from the reactor, or from neighbouring wards by the Deep Line. A ward can be cleared for a while. It cannot be cleared for free. How each construct stands up to gunfire is in section 3.12.

### 2.9 Adaptation

The user asked for an AI that adapts. The cheap, legible form is a tactic ledger.
- The director tags what the player relies on: storm cover, cave routes, river travel, night moves, door cutting, power taps, killing watchers, hiding at the Count.
- When one tactic has beaten it a set number of times while the player is an Anomaly or worse, it fields a specific counter. If you always hide in storms, it clears the sky over you. If you always cut the same kind of door, the door changes. If you kill watchers, they stop coming alone.
- Each counter shows as a visible change, and the assistant notes it: "That is the third time. The door cycle is different."
- Counters persist. The ring forgives harm but keeps what it has learned.
- **It learns what a gun is.** A firearm is a tool to the ring until it has lost constructs to one. After that, a weapon in the hand near a construct or inside a site raises Attention by itself (section 3.12).

It is a table of about a dozen rows, not machine learning, and every row is a piece of authored content.

### 2.10 De-escalation

- **Attention** fades by itself, faster the further the player goes.
- **Obeying a signal** is immediate Regard at any level.
- **Restitution** works at every level: return it, mend it, replant it.
- **Absence** heals negative Standing slowly, measured in ring days without a new offence.
- **From Blight** the road back is long but always open: several ring days out of sight, or one large act of repair.
- **Death** clears the ward's Attention. To the ring, the blight there is over (section 3.11).
- **A purge** ends when its cause is gone, and not before (section 2.14).
- **Struck is the one exception to all of the above.** Nothing de-escalates it. Section 2.12 sets out how hard it is to reach and how clearly the ring warns first.

### 2.11 Co-op

- **Signature adds up.** Four suits, four radios and four sets of jump jets are louder than one. A careless team is noticed sooner.
- **Attention is local, so a team can split it.** One player draws a ward's attention while another works in the next. This is the heist structure, and it is the best argument for co-op in this design.
- **Standing is shared.** One expedition has one reputation. A player who goes loud spends it for everyone. Among friends this is a feature, and it is how the two play styles collide inside one team.
- **Isolation is a co-op weapon.** At Trespasser the ring degrades the team's shared markers and voice link near live spires. At Blight it separates players with doors and weather. Each suit assistant belongs to one player, so the ring can make two assistants disagree.
- **Responses scale gently:** more watchers per ward with more players, never more than the ward's stock.
- **Solo and co-op use the same rules.** A solo player gets the ship's analyst as the second pair of eyes that a team gets from each other.
- **The bottom of the ladder in co-op** falls on the player who earns it, not on the team by default. The rules are in section 2.12.

### 2.12 Reputation: what the ring values

Added on 2026-11-21 at the user's request: "We can introduce a sort of reputation mechanic for the ring", with good acts (repairing systems, farming, planting), neutral acts (exploring outside restricted zones, light terraforming) and bad acts (turning off systems, harmful farming, destroying drones, killing too many animals without replacement). Revised the same day for the user's second answers: a ledger for each ward; harmful farming as "farming in ways that hurt the environment, same as today"; and a bottom state that cannot be recovered from but is hard to reach.

**Standing is the reputation.** It stays one value for the expedition. Attention stays separate, and the reason is worth stating: Attention answers "is something happening here now?" and Standing answers "what are you?". Being loud raises Attention and costs no reputation. Felling a grove in silence costs reputation and raises no Attention until the ring finds out.

**Two levels (decided 2026-11-21).** Each ward also keeps its own ledger of what the expedition has done there.
- Acts against things that serve the whole ring (Wall Engines, spires, the field, the Veil, the Lamp) are written to Standing in full.
- Acts in the garden and the ground (planting, taking, digging, farming, local machines) are written to the ward's ledger in full and to Standing at about a quarter.
- The class the ring applies inside a ward is Standing shifted by that ward's ledger, by one band at most. A player can be a Stray on the ring and a Trespasser in the ward they stripped.

#### The garden ethic

The ring does not value obedience, and it does not value "more". It values balance, because the garden is its charge and every living thing in it is on the books.

**Why a ring cares more than a planet would.** This is the story reason behind every farming rule below.
- A planet forgives bad farming, given time. Rock weathers into new soil, rivers carry damage away and the sea is vast. The ring has none of that.
- **Its soil was laid, not weathered.** There is no bedrock making more. What washes off a slope is gone.
- **Its rivers are plumbing.** They begin at outfalls in the walls and end in a closed sea, and the engines draw the rain from that sea. Whatever is washed into a river comes back as weather over the whole cell.
- **Its sea is a basin.** Nothing dilutes for ever.
- On the ring there is no "away" and no "downstream". Careless farming does, on a small scale, exactly what the Lamplighter has spent an age preventing.

**Good farming is what a careful farmer does today.**
- Rotating and mixing crops, and resting ground.
- Keeping soil covered.
- Terracing, or following the contour, on slopes.
- Using clearings and open ground instead of clearing forest.
- Leaving a strip of growth along water.
- Returning waste to the soil.
- Taking only the water a stream can spare.

**Harmful farming is what damages land today** (the user's definition).
- Working the same ground with the same crop until it is exhausted.
- Leaving soil bare so that rain strips it, worst on slopes. The project already has rain and wetness to drive this.
- Felling forest for fields, or burning to clear.
- Draining or filling wetlands.
- Fertiliser and pesticide running off into rivers.
- Drawing a stream down.

**The shortcut, and why the story wants it.** The fabricator can make fertiliser and pesticide, and they work: yields rise at once. The harm arrives hours or days later, downstream. The ship has a reason to push for it: its charter asks whether this ground can feed settlers (section 1.7), and it wants yield figures. So the pressure to farm badly comes from the mission, and the cost falls on the garden.

**The ship's crops are not harmful in themselves.** An earlier draft treated them as foreign to the garden; the user's definition replaces that. A crop that escapes its plot and spreads through a ward is one more imbalance for the tally to find, and a small one.

**The rest of the ethic.**
- **Planting is good** when it closes a shortfall with species that belong in that habitat: a storm scar replanted, a felled stand put back, the megastorm's wake reseeded.
- **"Too many animals without replacement" is a census balance.** Each species in a ward has a band it is expected to stay in. Hunting inside the rate at which the population replaces itself costs nothing. Driving a species below its band is harm for as long as it stays there. Letting it recover, or restocking it, ends the harm.
- **Light terraforming is neutral** because animals dig too. Each ward has a daily allowance of moved ground, well away from structures.
- **Taking dead things is neutral** because they are no longer on the books (rule 3).

**The dawn tally.** Living things change slowly, so the ring reconciles them slowly. Once a ring day, as dawn crosses a ward, the Count's totals for the ward's living things are checked against its books. Planting, felling, farming and hunting register then, not at the next pulse.

#### The acts

Weights are starting values on the -100 to +100 scale of section 2.5. The farming rows are read from what the farming model of section 3.13 measures on each plot. The ring judges results, not methods.

*Good.*

| Act | Weight | Scope | Does it fade? | Sensed by | Registers |
|---|---|---|---|---|---|
| Restoring a machine that serves the ring: a Wall Engine, a dead spire, a Veil blade | +15 to +25 | Ring | No | Lines: the machine reports itself whole | At once. Confirmed when it next does its work: a spire at the next eclipse, an engine as its weather changes over hours. |
| Mending a local machine: a relay, a door, a socket, a lift | +3 to +6 | Ward | No | Thresholds | At once |
| Helping a mender: bringing a part it lacks, clearing its route | +2 to +4 | Ward | No | Thresholds | At once |
| Planting species that belong to the habitat, where the ward is short | +0.1 to +0.3 a plant, up to the ward's shortfall | Ward | No, while the plant lives | The dawn tally | First credit at the next dawn. The rest as the plant establishes, at about 5 and 20 ring days. |
| Farming sustainably: rotation and mixed planting, soil kept covered, terraces on slopes, a buffer left along water, plots in clearings | +1 to +2 a ring day for a healthy plot, up to a ward cap | Ward | It lapses if the plot is neglected | The dawn tally | Each dawn |
| Restoring land that someone or something else damaged: a storm scar, the megastorm's wake, bare ground covered and rested | +1 to +5 by area | Ward | No | The dawn tally | As it establishes |
| Restocking a species, or leaving it to recover (if animals ship) | Ends the deficit; up to +3 beyond | Ward | No | The dawn tally | As the count recovers |
| Putting it back: returning a part, refilling an excavation, replanting what was felled, cleaning a fouled bank | Cancels up to three quarters of the original harm | As the harm | Not applicable | The sense that caught the harm | When that sense next reports |
| Obeying a boundary signal | +3, less for each repeat at one site in a day | Ward, with a small share to the ring | No | The sense that gave the signal | At once |
| Standing for the Count: a clean count in the open | +0.2, at most +2 a ring day | Ring | No | The Count | At the pulse |
| Mending the Veil | Story-scale | Ring | No | Lines | At once |

*Neutral.*

| Act | Weight | Scope | Does it fade? | Sensed by | Registers |
|---|---|---|---|---|---|
| Exploring outside restricted places; going through open doors | 0 | None | Not applicable | Thresholds, at sites | Not applicable |
| Light terraforming: a tent platform, a fire pit, a short trench or berm, inside the ward's daily allowance and away from structures | 0 | Ward | The allowance refills each day | The floor | Seconds |
| Foraging, fishing and hunting inside the ward's tolerance | 0 | Ward | The tolerance regrows | The dawn tally | Next dawn |
| Taking dead things | 0 | None | Not applicable | Not counted | Not applicable |
| A small fire of deadwood; a camp; a rated shelter on open ground | 0 | Ward | Not applicable | The Count sees the heat | Next pulse |
| Being loud: power, radio, engines, gunfire at nothing | 0 to reputation; it raises Attention | Ward | Attention fades in minutes | The Count; constructs nearby | Next pulse, or at once |

*Bad.*

| Act | Weight | Scope | Does it fade? | Sensed by | Registers |
|---|---|---|---|---|---|
| Switching off or stripping a machine that serves the ring: a spire's feed, a Wall Engine, a field projector | -20 to -40 | Ring | Not until it is restored | Lines and thresholds | At once. The whole ring sees it at the next eclipse or as the weather turns. |
| Switching off or stripping a local machine | -4 to -8 | Ward | Slowly, to a residue | Thresholds | At once |
| Destroying a construct: watcher -8, warden -12, mender -15 | As listed | Ward, half to the ring | Slowly, to a residue | The Count finds it missing | The next pulse, so up to 5 minutes. At once if witnessed or done with a powered weapon. |
| Forcing a door or cutting into a site | -5 | Ward | Slowly | Thresholds | At once |
| Tapping power; riding the Deep Line uninvited | -3 to -6 a time | Ward for power, ring for transit | Slowly | Lines | At once |
| Heavy excavation; any excavation near a structure | -2 to -15 by volume and nearness | Ward | Slowly; sooner if refilled | The floor | Seconds |
| Explosives | -10 and up | Ward | Slowly | The floor | Seconds |
| Felling forest beyond tolerance, for fields or for timber; burning to clear | -1 to -10 by area | Ward | Until regrown or replanted | The dawn tally. A large fire is seen at the next pulse. | Next dawn; next pulse |
| Exhausting soil: the same crop on the same ground without rest | -1 a ring day once the ground is spent | Ward | It ends when the ground is rested or its crop changed | The dawn tally | After several harvests |
| Leaving soil bare to erode, worst on slopes and in rain | -1 to -3 a ring day while it is bare | Ward | The daily harm ends when the ground is covered. Soil already lost stays lost, as a residue. | The dawn tally | Each dawn; sooner after rain |
| Chemical runoff: fabricated fertiliser or pesticide on ground that drains to a river | -2 to -6 a ring day while it flows | The ward, every ward downstream, and a share to the ring | It fades some days after it stops | Lines: the rivers are its plumbing, and it tastes what comes back | Hours to days: the time the water takes to reach the works |
| Draining or filling a wetland or pond | -5 to -15 | Ward | Until it is restored | The floor hears the digging; the dawn tally finds the loss | Seconds; next dawn |
| Drawing a stream down for irrigation | -1 to -3 a ring day | The ward and those downstream | It ends when the drawing stops | Lines | Hours |
| Killing animals faster than they are replaced (if animals ship) | -1 to -3 a ring day while a species is below its band | Ward | It ends when the count recovers | The dawn tally | Each dawn |
| Forcing the field with a craft after the Closing | -25 | Ring | Slowly | The field | At once |
| Touching the Lamp (rule 6) | Straight to Blight, and no further | Ring | Very slowly | Lines | At once |

**How harm fades.**
- A single harm heals slowly while the ring has nothing new against the player, but only down to a residue of about a quarter. The residue goes only by putting the thing right.
- A standing harm, such as an eroding field or a hunted-out species, does not heal at all until it is corrected.
- **Below -75, time alone stops helping.** From there only repair and restitution raise Standing. This is the first sign that the bottom of the ladder is different.

This is what "the ring forgives but does not forget" means in numbers.

#### Thresholds

These are the bands of section 2.5, measured on Standing as shifted by the ward's ledger.

| Class | Standing | The short version | Recoverable? |
|---|---|---|---|
| Guest | +60 and up | The house serves you | |
| Stray | +25 to +60 | The house lets you be | |
| Anomaly | -25 to +25 | The house is watching to see which you are | |
| Trespasser | -60 to -25 | The house obstructs you | Yes |
| Blight | -90 to -60 | The house is trying to remove you | Yes. Below -75, by repair only. |
| The last count | -90 and down | The house is deciding | Yes: by standing down |
| Struck | Not a number | The house has decided | **No** |

The megastorm is not on this table. It is an event (section 2.14).

#### The bottom of the ladder

Decided by the user on 2026-11-21: the bottom state is irredeemable, every rung above it is recoverable, and reaching it must take sustained, deliberate harm with clear warnings on the way down. The working name is Struck, as in "struck from the count" (section 2.13).

**What it is.** Not a lower number. It is the ring closing the account. It stops weighing what the player does.

**Reaching it takes three things, in order.** No single act can do it, and no accident can.
1. **Standing at or below -90.** By the weights above, that is about three ring-serving machines destroyed, or a few dozen constructs, with nothing put right. Touching the Lamp takes a player to Blight and stops there.
2. **The last count.** At -90 the ring does something it does at no other time: it stops. For three ring days it holds its fire in the ward, and waits.
3. **One more serious harm, committed during the last count.** Destroying or switching off a ring-serving machine, destroying a construct, touching the Lamp, or forcing the field. That act, made after the clearest warning the ring can give, is the point of no return.

If the three days pass without one, the last count ends, Standing is set to -80 and recovery goes on as usual. A later fall to -90 begins another last count on the same terms.

**The warnings on the way down** (explained; user, 2026-11-21). Each step is something the ring does and something the assistant says, and each is stronger than the last. The assistant speaks in its own words. It never uses the ring's names for the rungs, which the player learns from the ring, late.

| Where | What the ring does | What the assistant says, once |
|---|---|---|
| Into Trespasser (-25) | The "stop" signal at sites. Doors close. Entry lights go dark as the player approaches. | Its estimate changes: "It is obstructing us." |
| Into Blight (-60) | A tone through the floor. Site lights go white. Purgers. | "It is using force against us. I have no earlier record of that." |
| Below -75 | Nothing new. Staying away no longer changes anything. | "Time away has stopped improving its behaviour. Only repair has, in what I have recorded." |
| The last count (-90) | It stops. The purgers withdraw. Every site in the district shows steady white. At each pass the pulse holds overhead for some seconds, which it never does otherwise. The floor carries one long tone at the start. | The first of the two lines the user asked for: "Its behaviour has changed in a way I have not recorded before. It has stopped attacking, and it is counting us separately at every pulse. My reading, with high confidence: further serious harm now will not be forgiven." |
| As the player begins a serious harm during the last count | Nothing. It waits. | The second line: "If you do this, I do not expect it to be undone." |
| Struck | If it has words by then, it uses them: "You are struck from the count." Lights go out at every site as the player approaches, from then on. | It reports what it saw and heard, and that its estimate no longer changes with anything the player does. |

The silence is the design. A player in the last count is not being chased, so the choice they make there is one they can recognise as a choice. Both spoken lines are authored and critical, so they are never reworded and never skipped. The assistant stays inside its honesty rule (section 6): it gives its reading and its confidence, not the ring's intent.

**What being struck changes.**
- Reputation stops. Nothing the player does, good or bad, is weighed again.
- The lethal response of the Blight row applies in every ward.
- No door, socket, transit or shelter is ever offered. The ring's buildings do not open as the megastorm comes.
- A purge begins: the megastorm forms and comes for them, and comes round again for as long as they live (section 2.14).
- The Lamplighter never speaks to that player. The Counted, quiet door and keeping endings are closed to them. Two endings remain: the keys, and the breach, which gets them home by force (section 1.9; decided 2026-11-21).
- It is still a whole game: the war of dark wards described in section 4, with no way to end it but winning.

**Climbing back, from everywhere else.**
- **From Trespasser:** stop, obey the next signal, and put back what was taken. A ring day or two.
- **From Blight, above -75:** several ring days out of sight, or one large repair. Leaving the damaged ward is not enough by itself, because its ledger stays.
- **From Blight, below -75:** repair and restitution only.
- **From the last count:** do no serious harm for three ring days.
- **A redeemed player** can reach Stray on absence and small repairs. Guest has a harder gate: every ring-serving machine the expedition broke must be working again. A house does not seat a guest who has left it broken. What stays after redemption:
  - the counters the ring learned (section 2.9);
  - the residue in each ward's ledger until that ward is put right;
  - a longer watch: watchers follow a returned player for some ring days after they would have left anyone else alone.
- What the player sees when it works: menders that used to stop and wait as they approached go on with their work.

#### Feedback, co-op, exploits and systems

**How the player sees reputation.** No number, ever. The ring shows it, and the assistant puts it into one plain line.
- **The assistant's estimate** (explained; user, 2026-11-21). One line in its own words for how the resident system is treating the expedition, with its confidence: ignoring us, watching us, letting us be, accommodating us, obstructing us, using force against us. It is spoken once when it changes and otherwise sits on a page of the suit with the date of the last change. It is not a meter and it does not say how close the next change is.
- **The doors.** Open, closed, or opening as the player arrives.
- **The lights.** A site's light colour shows Attention (section 2.2). How its lights behave towards the player shows Standing: at Stray and above the entry lights come up as the player approaches, and at Trespasser they go dark. No legend explains either.
- **The constructs.** How far a watcher keeps off. Whether a mender carries on, waits, or withdraws.
- **Changes of class are events the ring makes:** a tone through the floor and a change at the nearest site. The assistant logs them and changes its estimate.
- **The ring itself.** A dark gap in the wall lights relit. A storm that has stood for centuries clearing. The Lamp a little brighter at the next eclipse.

**Co-op.**
- **The expedition shares one Standing.** Every act by any player is written to it in full. The story's gates read it.
- **Ordinary acts count for the expedition** (decided 2026-11-21). The host has an option that confines a friend's acts to that friend's own trace.
- **Each player also has a trace.** The ring tells suits apart. A player's own acts shift how the ring treats that player locally, by one band at most. The one who shot the watcher is the one the next watcher follows, and the door may close for them while it stays open for a teammate elsewhere in the ward.
- **Presence spreads.** Standing beside an offender when the Count passes puts a little of their trace on the bystander. The ring counts who was there.

**Co-op and the bottom of the ladder** (decided 2026-11-21: being struck is personal).
- **The last count is the expedition's.** It begins on the shared Standing, and every player's assistant says so, wherever they are. No one is struck without the whole team having been warned.
- **Being struck is personal.** It falls on the player who commits the serious harm during the last count, and on any player who joins in before it ends. One friend cannot doom the others.
- **The expedition itself is struck** only when its host is, or when every member is.
- **For the players who are not struck,** the shared Standing stays recoverable. But company counts: a party that includes a struck player is treated as Blight wherever they are together, and no door opens for it. The Counted and keeping endings need every player present to be unstruck.
- **A friend who joins later** arrives with an empty trace and is never struck by inheritance. They take on the expedition's shared Standing, and a purge if one is running. If the expedition itself is struck, they are joining a war: personally clean, but in a house where no door will open while they keep that company. There is no warning on the join screen (hidden; user, 2026-11-21). They find out the way the others did.
- **A struck player's state belongs to that world.** It does not follow them into another host's game.

**Exploits to guard against.**

| Exploit | Guard |
|---|---|
| Planting spam | Credit only up to the ward's shortfall, only for species that belong in that habitat, and only as plants establish over days. Planting into a full ward earns nothing. |
| Fell it and replant it | Replanting what the expedition felled is restitution, which cancels at most three quarters of the harm. The loop always loses. |
| Repair it, break it, repair it | A machine pays its repair credit once. Breaking it costs more than the repair gave, and the second repair is restitution. |
| A token plot farmed well for credit | The ward cap. A plot earns in proportion to what it feeds, and one expedition can only eat so much. |
| Farming a door for obedience | Credit falls with each repeat at one site in a day |
| Standing idle in the open for clean counts | A small daily cap, and no credit on a day with any harm |
| Hunt a species out, then restock it | Restocking ends the deficit and no more, unless the species was short before the expedition came |
| One player offends while another collects | All acts are written to the shared Standing. The trace is the only personal part, and it is bounded. |
| Strip a ward and move on | The ward's ledger stays. Ring-serving machines count ring-wide. The Guest gate needs the damage undone. |
| Dying to wipe the slate | Death clears Attention only, never reputation |
| Doing it where the ring cannot see | Acts in a dark ward or under a parked storm are not unseen for ever. They register when the ward is next tallied in clear conditions. The delay is real and the debt is too. |
| Riding the last count as a truce | The ring holds its fire, but only in that ward and only while the player does no serious harm. Three ring days later Standing is still deep in Blight. |

**How it maps onto what exists and what is planned.**
- **Vegetation and scatter (T5, T6).** Trees and plants are placed deterministically from the seed. Felling and planting need a vegetation edit store: removals and additions keyed by tile and instance, persisted like surface edits. A ward's baseline is whatever the seed generates, so the tally never counts a ward: it sums the edit store's differences by species. That keeps it cheap and exact. With the whole garden simulated (section 3.13), the differences also include every garden cell that is off balance. Each carries its cause, so the expedition is charged only for what it caused.
- **Farming, runoff and water.** Deep from the first version (user, 2026-11-21). The model, its water needs and how it sits on the frozen rivers are in section 3.13.
- **Editable terrain.** The surface edit store already journals every edit. Moved volume per ward per day comes straight from the journal, and nearness to a structure from the site registries' existing constant-time lookups.
- **Wall Engines and spires.** Machine state is saved story state (7.5 item 9). Spire state feeds the eclipse's per-site output, and an engine's state feeds its anomaly's strength.
- **Constructs.** Each ward's stock is a few counters. A missing construct is a difference found at the pulse.
- **Animals.** A later group, in the style of the T9 contingent items. The balance works on a population count per species per ward with a regrowth rate, with individuals drawn from that count near players, not on a full simulation. Until animals ship, the animal rows above lie dormant and cost nothing.
- **The last count** needs one small visual, the pulse holding overhead, which is the same grid shader change as the optional cue in section 2.2.
- **The director.** Reputation is the slow half of the director (7.3). Every act is a serialisable command, which the co-op contract of 7.4 already requires.

**Where the earlier iteration questions stand.** A ledger for each ward: yes (user, 2026-11-21). The ship's crops as foreign: replaced by the user's definition of harmful farming. Co-op: decided as above. Two were never answered and are **assumed, pending the user**: reputation is shown without a raw number, and a Guest must first undo their own damage to the ring's machines.

### 2.13 Names for the ladder

Adopted by the user on 2026-11-21. The names come from the ring's own three vocabularies: the census (it counts), the garden (it tends) and the house (it keeps).

| Rung | Name | Where the word comes from | It replaces |
|---|---|---|---|
| Top, reached in the story | **Counted** | The census. It pays off the Count. | (unchanged) |
| Second good rung | **Guest** | The house. It sets up the inheritance clause. | (unchanged) |
| First good rung | **Stray** | An animal that is allowed to stay | "Tolerated" (retired) |
| The fork | Anomaly | A working label for the undecided middle; the ring has no word for it | (unchanged) |
| First bad rung | **Trespasser** | The house | (unchanged) |
| Second bad rung, and the word for the destructive play style | **Blight** | The garden: a sickness that spreads. "It is treating us as a blight." | "Infection" (retired), and the shorthand noted in the names table of section 0 |
| Bottom | **Struck** | The census: "struck from the count" | Purge, as a rung |

**The two lines that frame the ladder** are a pair, spoken by the ring if it has words by then: "You are counted." and "You are struck from the count."

The alternatives that were considered are in the history of this file (commit 76c9ed0). The megastorm as a purge keeps its working name, the Fever. It is not a rung (section 2.14).

### 2.14 The megastorm: a purge or a misfire

Decided by the user on 2026-11-21: "Since the megastorm is reserved for a purge, or a glitch, destruction will be realistic." This replaces two earlier positions: the megastorm design's premise of a threat that is ever-present once triggered, and this plan's Fever that ran from the end of Act 2 to an ending.

**It is never weather.** Ordinary storms are the ring's weather. A megastorm happens for one of two reasons only.

| | A purge (the Fever) | A misfire |
|---|---|---|
| **What it is** | The ring's deliberate last resort | The same machinery firing with no order given |
| **Cause** | A player struck from the count. In the story, the ship's forced lander (question 1 in section 9.8). | Ageing Wall Engines |
| **How far it goes** | From where it forms until it has passed over its cause. If the cause survives, it keeps coming round. | A few hundred to about 3,000 km, until the Lamplighter can stand down the engines ahead of it |
| **How often** | Once in the story at most. Without end for the Struck. | Rare: none to two in a long campaign, on the ring as found |
| **Can the player cause one?** | Yes, by being struck | Yes: stripping or sabotaging Wall Engines raises the odds |
| **Can the player prevent or stop one?** | Not their own. The story's purge ends when its cause is gone. | Yes: mended and tended engines misfire less, and a misfire can be stopped by hand |

**How a megastorm is made (new lore).** The Wall Engines make it, in relay. Each pair of engines, one on each wall, raises the wall of cloud across the ring's width and hands it on to the next pair. A purge is every pair doing this in order, on the Lamplighter's instruction. This fits what is built: the wall spans the ring from wall to wall and climbs the wall faces, and the walls are where the engines are.

**What a misfire is.** The engines are tens of thousands of years old, and six are already dead. Now and then a worn pair fires its part of the sequence with no order given. Its neighbours were built to catch the wall and pass it on, and they do. The Lamplighter stops it the only way it can, by standing down the engines ahead, and that takes time and engines that still obey. A misfire is the ring's decline made visible. The Lamplighter can no more prevent one than a person can prevent a seizure.
- **How rare.** Each engine pair has a seeded chance, so misfires are fixed by the world's seed and the state of its machines, and are the same for every player in a world. Proposed for the ring as found: none in the first fifteen hours of play, and none to two in a long campaign.
- **Where.** Anywhere on the ring. Most are seen from far off, as a dark bar on the arch and afterwards as a scar. A misfire that reaches the player is the exception.
- **The player can raise the odds.** Every Wall Engine stripped or sabotaged misfires more readily. A misfire the expedition caused is charged to it.
- **The player can lower them.** Every engine mended or tended misfires less. This is part of the keeping.
- **The player can stop one.** The wall moves at 350 m/s and the Deep Line is faster. A player can ride ahead of the wall to a station it has not reached and stand its engines down by hand. It is the largest single act of regard in the game. It needs the Deep Line, the knowledge of how, and nerve.

**A purge.**
- **It forms far off and comes.** The megastorm design forms the wall 600 to 2,000 km from its target, and the floor hums from 105 minutes out. That warning is unchanged.
- **It cannot be aimed.** It is a broom and not a blade. It levels everything between where it forms and its cause.
- **It ends when its cause is gone.** When the wall has passed over what it was sent for, the engines ahead stand down and it fades.
- **For the Struck it does not end.** If they are still there at the next Count, it keeps coming round, a lap every 25 hours of play, until they are dead, gone through the breach, or holding the keys. For them, and only for them, the megastorm design's "ever-present threat" is still true.

**What Standing changes when a wall comes,** whichever kind it is.
- **A Guest** finds the ring's own buildings open as the wall approaches. The megastorm design already states that authored structures are never flattened.
- **A Blight or a Struck player** finds them sealed, and must dig, build to a rating, or outrun it.
- **Inside the wall is the one place on the ring where the Count sees nothing.** A player who can survive in there for the 71 seconds of the wall and the long rain behind it can do anything.

**Why it is a fever and not a weapon.** A purge levels the garden the Lamplighter exists to keep. It strains the engines, which makes misfires likelier afterwards, and it dims the Lamp. The Lamplighter resorts to it the way a body resorts to a temperature it cannot sustain. This is what gives the blight path its moral weight.

**Destruction is realistic** (the user's decision).
- **The numbers already decided stand:** 60 kPa in the open and 0.6 of that in ordinary lee. At those pressures forest goes down almost completely. Trees stay standing only in deep shelter: narrow valleys, the lee of cliffs, and beside the ring's own structures.
- **What a sweep leaves:** windthrown forest, soil stripped from slopes, brown rivers, and every unrated building and unsheltered farm flattened.
- **It comes back at the garden's speed** (section 3.13): grass within a ring day, scrub in about eight, a young wood in about forty. Mature forest does not return inside a campaign, and soil lost from a slope does not return at all.
- **Fallen timber is dead, and dead things are free** (rule 3). A sweep is also the largest windfall of salvage in the game.
- **The scar shows on the arch** for the rest of the game.

**What that means for the late game.**

| What happened | What the ring looks like afterwards |
|---|---|
| A misfire | A levelled strip up to about 3,000 km long, a tenth of the ring, somewhere on the arch |
| The story's purge, if it stays in (question 1 in section 9.8) | A levelled country one to two thousand kilometres long between where the wall formed and the ship's lander. The player's Act 2 ground is in it. |
| A struck expedition | Every exposed part of the ring levelled within one lap, and kept as scrub by each lap after. The blight ending is played in a wasteland of the player's own making. |

- **Regrowing a scar is credited work** (section 2.12), and it is the first great task of the keeping.
- **In the garden model** (section 3.13) a megastorm stays a ring term. The saved record of each sweep, with how exposed a place is, gives the damage and the recovery at any spot with nothing stored for each cell. Each sweep carries its cause: the ring's own for a misfire, the ship's for the story's purge.
- **One new thing to make:** forest that looks windthrown. Fallen trunks are a scatter class the project does not have.

**What is built today, and what changes.** In the current game the megastorm has no cause at all. Nothing in the weather creates one; it exists only when the grave-menu Spawn button or the trigger call starts it. That stays true.

| Built (megastorm design and handoff) | What changes |
|---|---|
| One megastorm at a time, started by a trigger call that is idempotent for each trigger id, and saved with the world | The director makes the call, with one trigger id for each purge or misfire |
| After forming it laps for ever. A Stop call fades it out, added "for story flexibility". | Every megastorm now ends. Stop is called when a purge's cause is gone or a misfire is damped. Lapping for ever is left only for a struck player. |
| No record of past megastorms. The design notes that "a list later is a schema bump". | A saved list of past sweeps: where and when each started and stopped. The garden needs it. This is that schema bump. |
| Started in play only from the grave-menu Spawn and Stop buttons | Those stay as debug tools. Misfires come from a schedule fixed by the seed and the state of the machines. |
| Hazards are values and events only, with no damage, by the user's decision of 2026-10-29 | Damage to players, to structures by the ratings already decided, to farms and to the garden |
| The suit lines and the "Structural resonance" entry | Unchanged |
| Its lore hooks fix no canon | The relay of Wall Engines is new lore. The design doc's opening premise, "an ever-present threat once it is triggered", needs a dated note that the user changed it on 2026-11-21. This plan does not edit that doc. |

## 3. Core loops at three timescales

### 3.1 Three clocks and one event

The pacing rests on three clocks. All three already run in the engine, are deterministic and are saved with the world. The megastorm is not a clock: it is an event (section 2.14).

| Clock | Period | What it does to play | State |
|---|---|---|---|
| **The pulse** | 5 minutes | The rhythm of the moment: when to be loud, when to be still | Built (visual and clock) |
| **The day** | 75 minutes, a little over half an hour of light | The rhythm of an expedition: light, temperature, solar charging, and nights when the pulse can be watched | Built |
| **The eclipse** | 75 minutes of totality every 7.83 hours; the first comes about 3.3 hours into a new world | The session event. The ring does not count. It is pitch dark before ignition and after the collapse. The spires are sacred. | Built |
| **The megastorm** (an event, not a clock) | Only as a purge or a misfire. When one comes, the floor hums from 105 minutes out, the wall lasts 71 seconds and the rain behind it 16 minutes. | The exception: shelter, survive, and afterwards live in a levelled country | Built without damage |

If play tests want eclipses more often, the celestial time scale is an existing setting. That is tuning, not design.

### 3.2 The moment-to-moment loop (seconds to minutes)

**Move, read, act, be still.**

- **Move.** Walking is 2 m/s and sprinting 7 m/s. At that speed the terrain is the content: slope, footing, river class, canopy, the lee of a ridge.
- **Read.** The sky for weather, the arch for the pulse and the megastorm, the spire lights and the suit's district reading for the ring's mood, the suit for power and warmth, the assistant's pulse countdown.
- **Act.** Forage, salvage, scan, climb, cut, open, carry.
- **Be still.** When the pulse comes, either go dark or stand to be counted.

A minute of it, as it should feel:

> You are forty metres up a dead relay pylon with the cutter running, and the assistant says "ninety seconds." The bracket is nearly through. To the east the arch is already brightening where the pulse is coming round. You stop cutting at twenty seconds, kill the suit, and hang on the rope in the wind with your own breathing for company. A line of light goes overhead faster than anything should move. You count to ten. The suit comes back up, and the bracket has four minutes and forty-eight seconds left to live.

### 3.3 The expedition loop (one session: one to three ring days)

1. **Plan at the foothold.** Pick a target. Choose a loadout, which is a choice between quiet and capable (section 3.6). Read the route: terrain, rivers, weather coming, the ward's mood, the pulse timetable, time to the next eclipse.
2. **Go out.** On foot an expedition owns a circle of about 10 km: a ring day of walking is 9 km. A glide or a river stretches that to tens of kilometres one way.
3. **Do the thing.** Most targets are a threshold: a site, a wreck, a storm core, a climb.
4. **Live with it.** Whatever Attention the job raised now shapes the way back.
5. **Return, or push on** to a cache or a forward camp.
6. **Bank it.** Repair, fabricate, file what the assistant learned, save.

The limits that make an expedition end are carry weight, suit power, water and daylight. None of them is a timer the player watches; all of them are reasons to turn round.

### 3.4 The campaign loop (tens of hours)

1. A ward becomes known: its terrain, its spire, its rules, its blind spots.
2. Knowledge, gear and Standing (or its opposite, section 3.9) make a harder site reachable.
3. A spine discovery changes the whole ring: the sky closes, the ship's purpose comes out, the ring purges.
4. The changed ring makes the home wards feel different and sends the player further.
5. A megastorm, when one comes, turns the loop over: know where the wall is, be under something rated when it arrives, and then live in what it leaves.

### 3.5 Survival: the blend (decided 2026-11-21)

**Suit first, body second.** The suit's meters move fast and create tension in the moment. The body's meters move slowly and shape expeditions. While the suit has power it buffers the body: it holds warmth and stretches water. When the suit is shut down or damaged, the player falls back to being an animal: fire, shelter, dry clothes, a stream.

This has a consequence worth designing around. Being an animal is exactly what the ring tolerates. So the abide path naturally becomes the full-survival game, and the blight path naturally becomes the suit-and-power game. The user asked for a mix of both kinds of survival; this gives each kind a home.

| Meter | Timescale | Drains with | Restored by | Where it earns its place |
|---|---|---|---|---|
| **Suit power** | Hours on baseline, minutes under load | Everything the suit does | The lander, solar film by day, salvaged cells, ring sockets | Everywhere. It is the master resource and it is also the player's signature. |
| **Air** | Minutes, only where it applies | Sealed-suit time | Breathable air, tanks | Only where the air is not: underwater, above about 6 km on mountains and the walls, beyond the field, sealed or vented rooms, the megastorm's vacuum wake. Never a constant drain. |
| **Suit integrity** | Events | Falls, megastorm rain, constructs | Patches, the fabricator | It turns a tear into a problem wherever air or cold matters. |
| **Body temperature** | Tens of minutes | Cold, wet, wind, altitude, night; or heat in a drought | Suit heating (costs power), fire, shelter, dry gear | The wet world, weather and altitude systems all feed it. It is the meter that makes weather matter before weather is dangerous. |
| **Water** | About 90 minutes of play | Time, heat, exertion | Streams and lakes with treatment, the suit's recycler | Routes follow rivers. Droughts become real places. |
| **Food** | About 4 to 5 hours of play | Time, exertion, cold | Rations, forage, fish | It forces contact with rule 1. A hungry player must decide how to eat. |
| **Health and conditions** | Events | Falls, cold, drowning, combat | Rest at a camp, a medical kit | One health value plus a few named conditions (sprain, fracture, laceration, concussion, hypothermia), each with one clear effect on movement or carrying. |

Left out on purpose: sleep (a 75-minute day makes it absurd; rest exists only for healing and saving), and sanity, radiation and hygiene.

**The sealed start.** The expedition lands with helmets sealed, on tank air, under biosafety rules. The first objective is to sample the air. When the assistant clears it and the helmet opens, the muffled world becomes the real one in a single cut. It teaches the air meter, costs one audio filter, and gives the first hour a moment.

All rates here are starting points. They follow the project's rule of realism by default: a person in wet clothes at altitude should be in trouble on roughly the timescale a person would be.

**Three presets** (decided 2026-11-21) scale these rates and nothing else. The rules, the ring's behaviour and the story are the same on all three.

### 3.6 Gear

Most needs have a quiet answer and a capable one. Loadout is the choice between them.

| Need | Quiet | Capable |
|---|---|---|
| Light | Chemical light, fire | Suit lamp |
| Warmth | Fire, shelter, dry layers | Suit heater |
| Getting down or across | Rope, glide wing | Jump jets |
| Getting in | Another way round, or Standing | Cutter |
| Seeing | Eyes, passive scan | Active scan |
| Travel | Raft, glider | Rover, aircraft |
| Power | Solar film, by day and weather permitting | Generator, ring socket |

Core kit: the suit and its modules (cells, tank and rebreather, thermal layer, plating), a climbing set, the glide wing, the cutter, the scanner, a field repair kit, a water kit, and a sidearm with very little ammunition (section 3.12).

### 3.7 Scavenging and crafting

Four sources, each with a different cost:

| Source | Gives | Costs |
|---|---|---|
| **The ship** | Human equipment, rations, feedstock | Every drop crosses the field and is felt. After the Closing there are none. |
| **The garden** | Wood, fibre, stone, water, food | Rule 1: within a ward's tolerance it is free; beyond it, it is harm. |
| **The dead ring** | Cells, alloys, optics, cores: the things that cannot be made | Rule 3: free to take. But dead things lie in parked storms, droughts and dark wards. |
| **The live ring** | The same, better and nearer | Theft. Fast progress, lasting harm. |

- **Fabrication happens at a foothold.** The lander carries a fabricator that turns feedstock and salvage into human equipment. It is loud to the Count while it runs.
- **The field kit does repairs and consumables only.**
- **Ring technology cannot be fabricated.** It can be salvaged and adapted into hybrid gear. That keeps the best equipment tied to places, not to a recipe list.
- **Keep the recipe list short:** about thirty in the first full version. This is a game about where things come from, not about crafting trees.

### 3.8 The foothold, the camps and the ship

- **The foothold is on the ground.** The lander is the first home: power, fabricator, storage, save point, antenna. It is also the loudest thing the player owns.
- **Where to land is the first decision.** Offer three candidate sites with different terrain and different distances from a spire. It gives replays and co-op groups a real choice on day one.
- **Camps, not a fortress.** A megastorm flattens anything unrated in its path, and the megastorm design already gives ratings for materials. The right shape for player building is therefore several small, dug-in, rated shelters and caches, with maintenance as the cost. The user's megastorm notes ask for exactly this: infrastructure that fails "if not well maintained".
- **The pulse governor.** A mid-game foothold upgrade that shuts the camp down for each pulse automatically. A good example of progression that removes a chore the player has already mastered.
- **Shelter makes a triangle.** Dig (rule 2 and the floor sense), build to a rating (materials and upkeep), or use the ring's own buildings (Standing, or a break-in). Each play style has an answer and none is free.
- **The ship is a voice and a lifeline, not a level.** No ship interior is needed. After the Closing it can be reached only with real power: from the lander's antenna, from high ground or from a dark ward. Talking to home is loud.

### 3.9 Progression

Three tracks. The third has two forms, one for each play style.

| Track | What grows | Examples |
|---|---|---|
| **Gear** | What the player can survive and reach | Bigger cells, a rebreather for altitude, a rated shelter module, hybrid tools |
| **Knowledge** | What the player understands | The pulse timetable, the field log and its patterns, the signal vocabulary, the map of wards and blind spots, the Deep Line map, the database |
| **Standing** (abide) | What the ring will do for the player | Open doors, live sockets, transit, shelter in a megastorm, speech |
| **Leverage** (blight) | What the player can make the ring do | Stolen cores that force doors, overrides that hold a lift, dark wards held as territory |

**Every threshold has three keys: be let in, slip in or break in.** Standing, Knowledge and Leverage open the same places in different ways. That is the rule that keeps the two play styles on the same content.

### 3.10 Travel at each scale

| Scale | Means | Speed | Seen or heard? | Depends on |
|---|---|---|---|---|
| **To 10 km** | Walking, sprinting, climbing, swimming | 2 to 7 m/s | No | Built |
| **To 80 km, downhill** | Glide wing from height. At a glide ratio of 8, a 1 km ridge gives 8 km, a 5 km peak 40 km, the 10 km wall top 80 km. | 15 to 20 m/s | No | New, small. Climbing becomes an investment that pays out as distance, and the ring's huge verticals become useful. |
| **Along water** | Raft, later a boat. The river danger classes become the route: calm water is a road, torrent is a wall or a one-way express. | Current plus paddling | No | T9i (floating and flow) |
| **To 100 km over land** | Rover | 15 to 25 m/s | Heard through the floor the whole way | New, large |
| **Vertical, at sites** | Site lifts; the bore drop into a reactor cavern | 150 m/s (lift, decided) | Thresholds | Reactor underground (paused) |
| **Ring scale** | The Deep Line: capsules between reactor caverns along each of the six chains. At 1 g a 314 km hop takes about 6 minutes and half the ring about 42. | To 1.7 km/s per hop | Lines: the ring knows exactly where you are, and it controls the capsule | Reactor underground; the existing site teleport sequencer |
| **Anywhere, late** | Aircraft | 100 m/s and up | Seen by the Count, and by the field above the walls | New, large. The field was built with flight in mind. |

**Hijacking the Deep Line** is the risk the user described. Uninvited, a rider may arrive at the right cavern, at a holding bay, or stopped in the dark between stations while the ward wakes up. A Guest simply rides.

**The scale problem, stated plainly.** The ring is 31,416 km round. No campaign can use it all on foot. The plan is three nested ranges: a home circle on foot, a region by glide, river and rover, and the ring by the Deep Line. The spine uses the third on purpose (section 5).

### 3.11 Death and saving

Decided by the user on 2026-11-21:
- **Saving** happens at footholds and camps, with the existing autosave between.
- **Solo death** returns the player to the last foothold. Carried gear stays where they fell and can be fetched. Time has passed. The ward's Attention has dropped, because as far as the ring can tell the problem there is gone.
- **Co-op death** leaves the player down and recoverable by a teammate for a time, then follows the solo rule.
- A harder optional mode can treat each death as the loss of a crew member from a finite roster. That suits the fiction but should not be the default.

### 3.12 Weapons, ammunition and fighting

Reworked after the user's answer of 2026-11-21: firearms are in, slightly futuristic, with limited ammunition at the start. This replaces the first draft's "tools only" position.

**The arsenal** is small, and human-made first.

| Weapon | What it is | Fed by | Comes from |
|---|---|---|---|
| Sidearm | A caseless pistol | Rounds | Carried from the landing |
| Carbine | A caseless rifle with a smart optic | Rounds | The lander's locker; an early goal |
| Coil rifle | An electromagnetic marksman's rifle | Fabricated slugs, plus suit power for every shot | The fabricator, mid-game |
| Disruptor | A short-range discharge that stuns a construct | Suit power | The fabricator, mid-game |
| Charges | Survey explosives | Themselves | The ship; later the fabricator |
| Hybrids | Ring parts adapted into weapons, such as a warden's discharge head | Suit power | Late; salvage or theft |

The cutter stays a tool that can hurt a construct at arm's length.

**Ammunition is the scarce thing.**
- The expedition was equipped for wildlife, not for war. A starting figure to tune: two magazines for the sidearm, and three for the carbine once it is found.
- Nothing on the ring uses bullets. No construct drops ammunition and no site stores it. Every round came from home.
- There are two sources. Ship drops, each of which the field feels, and none after the Closing. And the fabricator, from mid-game, which makes rounds slowly from feedstock and is loud to the Count while it runs.
- Powered weapons turn the trade round. They never run dry while the suit has power, and every shot is seen.

So the choice in the hand is the choice of the whole game in small: **scarce and quiet, or plentiful and loud.**

**How a shot reaches the ring.** Its senses hear three different things, and the differences are the stealth game.

| | The Count | The floor | The air |
|---|---|---|---|
| A round | Nothing: it is not powered | Faint | Any construct within about a kilometre hears it, and so does a whole site from the inside |
| A powered shot | Seen at once, not at the next pulse. The grid already shows lightning crawling along its beams; a coil discharge is a small lightning. | Nothing | The same as a round |
| A charge | Seen | Very loud | Loud |

**The ring counts what is missing.** A census finds absences. A construct destroyed quietly is not missed until the next pulse passes and the Count comes up one short. A kill made with a round, unseen by a second construct, therefore buys up to five minutes before the ward reacts. A kill made with a powered weapon, or in front of a witness, buys none. This is the centre of stealth combat here, and it costs nothing new: it is the Count doing its job.

**What a gun does to Standing.**
- Carrying one costs nothing. To the ring it is a tool like any other, at first.
- Firing one costs nothing by itself, where nothing hears it.
- What is shot is what counts. A construct, a machine or a counted animal is harm under the rules that already exist. Destroying a construct is among the heaviest harms short of touching the Lamp.
- The ring learns what a gun is (section 2.9). After that, a weapon in the hand near its constructs or inside its sites raises Attention, and watchers keep a longer distance. Holstered, it goes back to being a tool.

**Constructs under fire.**

| Construct | Against firearms |
|---|---|
| Watcher | Fragile: one to three rounds. Cheap to kill and expensive to have killed. |
| Warden | Armoured against rounds except at its discharge head. A coil rifle or a disruptor is the practical answer. |
| Mender | Sturdy and harmless. Shooting one is vandalism and is read as vandalism. |
| Purger | Small and fast. It can be shot, with difficulty. While it lives, what kills the player is the sky, and a gun does not stop the sky. |

**Guns do not beat the ladder.** Every construct killed pushes Standing down, the ward restocks from its neighbours, and the tactic ledger answers: watchers come in pairs, wardens arrive first. A player can win every fight and still be losing.

**The promise to the abiding player.** The user's premise is that abiding means little to no fighting. Four rules guarantee it:
1. **The ring uses no lethal force on a player above Blight.** Watchers never attack. Wardens block and drain. A player whose Standing is above -60 is never shot at, struck or hunted.
2. **Carrying a weapon is never an offence.**
3. **Ammunition is too scarce at the start to make fighting the easy answer for anyone.** The first hours play the same for both styles, and the blight path opens as its supply does.
4. **Every job on the spine can be done without firing.** Each threshold keeps its three keys (section 3.9).

An abiding player's weapon is for the unexpected. Finishing the story with the starting magazines untouched should be possible, and worth marking.

**Hunting** with a firearm falls under rule 1 like any other taking. Within a ward's tolerance it is an animal feeding itself.

**What may be shot** (decided 2026-11-21): constructs, and animals if animals ship. There are no human enemies, and friendly fire is off by default in co-op.

### 3.13 Farming and the living garden

Decided by the user on 2026-11-21, in three answers, each of them larger than this plan recommended:
- farming is deep from its first version: a full soil, water and ecosystem model;
- the simulation reaches the whole garden, ring-wide, not only the farm;
- it runs fast enough that results show within a single session;
- both the garden's wild food plants and the ship's cultivars are farmed;
- soil and water tests are explained to the player.

This section is the design at plan level. It needs its own design doc before any work order.

**The principle.** The ring does not judge methods. It measures results: what the soil is doing, what left the field in the rain, what reached the river, what the forest is doing. Because the model is real, the reputation rows of section 2.12 need no list of approved practices.

#### Time

**Decided 2026-11-21: one ring day is one growing season, on one clock for the whole ring.** Four ring days make a year.

| | Step | In field time |
|---|---|---|
| The farm (fine ground) | Every pulse: fifteen steps a ring day | About six days a step |
| The garden (coarse cells) | Every dawn tally | One season a step |
| The ring's judgement | Every dawn tally (section 2.12) | It judges a season at a time |

**What a session shows.** A session is about two ring days.

| What the player does | When they see the result |
|---|---|
| Sows quick greens | Harvest in about 40 minutes |
| Sows a main crop | Harvest in one ring day, 75 minutes |
| Leaves a slope bare | Rills at the first rain; a brown stream below it within the hour |
| Abandons a plot | Grass over it by the next ring day |
| Clears a stand of trees | Scrub in about 8 ring days; a young wood in about 40. Mature forest does not come back inside a campaign. |

**Rain.** The weather runs in real time and the ground in field time, so one rule joins them: a ring day of weather delivers a season of water. A twenty-minute storm is about three wet weeks to a field.

**Why not two speeds.** A farm that ran faster near the player was considered and rejected.
- In co-op, two players in different places must agree what day it is for every plot.
- A plot that ages faster while watched can be exploited by standing beside it, and makes distant farms pointless.
- Two levels of detail on one clock give the saving without either problem (below).

**No catch-up.** The earlier draft let distant plots fall behind and catch up later. With fixed budgets that is unnecessary: the whole world steps together whenever it is running, and nothing is left behind. The only catching up is a joining player receiving a snapshot.

**The exaggeration, stated.** One season in 75 minutes is about 1,700 times the speed of life. It is the user's decision, and every process uses the same factor, so the relationships between them stay true.

#### The whole garden: how a ring-wide simulation stays tractable

The ring's floor is about ten million square kilometres. It cannot be simulated as plants, and it does not need to be.

**The baseline is the balance.** The generated garden is the state the Lamplighter has kept for an age. It is the equilibrium. The simulation stores only departures from it. Where nothing has disturbed the garden, nothing is stored and nothing is computed. Cost follows disturbance, not area.

**Four levels, one clock.**

| Level | Grain | Holds | Stored | Steps |
|---|---|---|---|---|
| **Ring terms** | The whole ring, one entry for each machine | The megastorm's passes, each Wall Engine's weather, the Lamp's brightness | A few hundred numbers. The megastorm needs none: its path is already saved. | Evaluated when asked |
| **Garden cells** | About 1 km: four by four of the existing vegetation tiles | Cover by class (canopy, shrub, grass, bare), stand age, soil stock, moisture, fire, pests, pollinators, escaped crops, and animal figures that lie dormant until animals ship | Sparse: only cells that are off balance, about 32 bytes each | Each dawn tally |
| **River overlay** | Each reach, lake and pond | Loads, withdrawals, pond levels | Sparse | Each pulse |
| **Fine ground** | 2 m cells and single plants | Plots, felled and planted trees, surface edits | Only where players have acted | Each pulse |

A summary for each of the 595 wards is derived from these for the reputation ledgers.

**The arithmetic.**
- Stored densely at 1 km the garden would be ten million cells and about 320 MB. It is never stored densely.
- A budget of 200,000 off-balance cells is 6.4 MB and one pass of a worker thread each ring day. Beyond the budget, the oldest and faintest departures settle to balance.
- A megastorm touches every cell it sweeps. It is therefore not stored in cells at all: its damage at any place is computed from how exposed the place is and how long ago a wall last passed, and the saved record of each sweep gives the second (section 2.14).
- The Wall Engines work the same way. Repairing one changes a single number, and the drought it parked greens over tens of ring days.

**How a garden cell behaves.**
- **It recovers towards its baseline:** bare ground to grass, to scrub, to canopy, at rates limited by its soil and moisture.
- **It is helped or harmed by its neighbours.** A clearing inside forest closes faster than the middle of a wide one. Fire, pests and escaped crops spread to the cells beside them.
- **It reads the ring terms** for drought, parked storm and megastorm wake, and the river overlay for water.
- **It steps from its neighbours' previous values, in whole-number arithmetic.** The order in which cells are visited cannot change the result, and every machine gets the same answer.

**Every departure carries its cause:** the expedition, the ring or the weather. A spread inherits the cause it spread from. Reputation charges the expedition only for what it caused, and mending anything else is credit.

**Promotion and demotion** between garden cells and fine ground.
- **Promotion.** When a player acts in a cell (tills, plants, fells, digs), fine state is created for the ground they touch. It is built from the cell's state, the baseline and a hashed variation, so the same cell always unpacks the same way.
- **Demotion.** When fine ground has had no player within about 2 km for some ring days and is not a tended plot, its totals (soil, cover, biomass, cause) are folded into its garden cell and the fine state is dropped.
- **Totals are conserved in both directions,** so nothing is gained or lost by walking away and coming back.
- **What demotion loses is identity:** which tree was felled. The forest closes over. A plot that someone still tends never demotes.

#### How the garden shows, without breaking the generation rules

**The rule kept.** Generated vegetation is a frozen baseline. Everything here is an overlay on it. With an empty overlay the output must be bit-identical to today's, and that is a test gate.

- **Near the player: one more factor in the scatter's density.** The scatter already accepts or rejects each candidate by a density from its habitat. The overlay supplies one more factor for each class from the garden cell, exactly 1 where the cell is at balance.
  - Thinning removes instances by their stable hash, so the same trees go first every time and nothing flickers.
  - Spreading beyond the baseline is limited to the candidates the generator already considers.
  - Stand age scales young trees.
  - Factors change in steps, at the tally, and tiles in view rebuild with the fade they already have. The grass modifier zones are the precedent.
  - To be checked against the T5 and T6 designs: how the tree spacing competition behaves when its density changes.
- **Fine ground overrides** the factor wherever it exists.
- **Further out: one ring-wide garden texture.** About 4 km a texel (7,854 by 80), on the canonical coordinates the whole-ring shadow masks already use, carrying greenness, canopy, bare ground and burn. The shared ground include samples it, so the near terrain, the proxies, the global terrain and the far view all take it with no switch for each. T6's far canopy term reads it as a weight on the forest.
- **Water** takes a tint for each reach and lake, and ponds a level that can move.
- **What this gives the vastness plan.** The arch gains a slow motion of its own: the megastorm's scar crossing forest country, a drought greening over a campaign.

#### The farm

**What the fine model holds.**

| Layer | State kept for each 2 m cell of a plot | What drives it | What the player sees |
|---|---|---|---|
| Soil nutrients | Nitrogen, phosphorus, potassium; organic matter | Crop uptake, legumes fixing nitrogen, compost and manure, fabricated fertiliser, leaching in rain | Crop vigour and colour; the soil test |
| Soil structure | Topsoil depth, compaction, soil life | Tillage, traffic, cover, roots, pesticide | Rain that soaks in or runs off; how the ground works |
| Soil water | Moisture | Rain, irrigation, evaporation and crop use, drainage by slope and structure | Wilting, waterlogging, cracked or sodden ground |
| Erosion | Soil lost, and where it went | Runoff, slope, bare fraction, structure | Rills after rain; a brown stream below the field; thinner soil each season |
| Crops | Species, growth stage, health, yield | Light (canopy shade counts), temperature, water, nutrients, crop family and what grew there before | The plants themselves, at each stage |
| Pests and disease | Pressure, by crop family | Area in one crop, repetition, nearby habitat for predators, pesticide | Damage on the crop; lost yield |
| Pollinators | Abundance near the plot | Flowering habitat within reach, pesticide | Fruiting crops that set or fail |

Pests and pollinators are population figures, not creatures, with small visible insects as dressing. Beyond the plot they are the garden cells' own figures, so a sprayed field thins the pollinators of the country round it.

**What is farmed** (decided 2026-11-21: both).

| | The garden's food plants | The ship's cultivars |
|---|---|---|
| What they are | The wild ancestors of crops, thirty thousand years unbred | Modern seed from the ship's stock |
| Yield | Low | High |
| Needs | Little: they suit their habitat | Hungry for water and nutrients |
| Against pests and weather | Hardy | Tender |
| What the ring makes of them | Nothing: they are its own | Nothing in themselves. If they escape and spread, the garden cells show it. |

The trade between them is the story's argument in small, and it is where the ship's demand for yield figures bites.

**Water: what farming needs, and what exists.**

| Farming needs | What exists | What is new |
|---|---|---|
| Rain on a plot | Weather is a pure function of seed and time | Sampling it for a plot at each step. No new state. |
| Somewhere for runoff to go | The rivers step bakes a drainage field and a river graph, with discharge, speed and a downstream link for every reach | Routing a plot's runoff to its reach |
| A river that carries what is put in it | The same graph | A load for each reach (silt, nutrient, chemical) that moves downstream at the reach's speed, is diluted by its discharge, and decays |
| Irrigation draw | Nothing. Hydrology is static. | A withdrawal entry against a reach, lake or pond |
| Ponds and wetlands that can be drained | Ponds are planned as still water in the landforms step (T7-11) | A level offset for each pond, and a check that notices a terrain edit breaching its rim |
| Water that looks changed | River, lake and pond surfaces with fixed levels and colours | A tint for each reach and lake (silt, bloom), and a pond surface that can move |

**Layered on the frozen river graph without changing generation.**
- **The frozen graph is the baseline and is never written to.** Everything above is an overlay of differences, keyed by reach, lake and pond and stored as game state.
- **Nothing the rivers were frozen with moves:** not the fingerprint, not the danger classes, not the collision.
- **River level and flow do not visibly change.** A farm draws litres a second from a river that carries cubic metres. The draw still counts in the books.
- **Ponds do visibly change,** because a pond is small enough for a farm to empty. That is new presentation work on top of T7-11.
- **Rivers and lakes change colour, not shape.**

**How this relates to T9i and the river freeze.**
- **The rivers freeze is the dependency, and it helps.** Overlay keys are reach and lake identities, which are stable once the step is frozen.
- **T9i is not a dependency.** It makes the current act on bodies and floating things. Farming reads the graph's numbers, not the current's physics.
- **T9a (small streams) matters** if plots are to draw from, and foul, water smaller than a trunk river.
- **T7-11 (ponds) is the dependency for wetlands.**

#### Reading it, owning it, saving it

**How the ring's senses read it.**
- **The dawn tally** reads each ward's garden cells and each plot's trend, by cause.
- **Lines** read the river's load where the water returns to the works, after the flow time.
- **The floor** hears powered tillage and the digging of channels.
- **The Count** sees pumps and powered equipment like any other load.
- A hand-dug channel and a gravity feed are silent. Once again the quiet way and the good way are the same way.

**Reading the ground** (explained; user, 2026-11-21). The player reads a farm first with their eyes: crop colour, standing water, rills, a brown stream. The suit adds two instruments, a soil probe and a water test, that give plain readings with numbers and units. The assistant reads them out and logs them for each plot. It gives no advice, and it never connects a reading to the ring's regard.

**Authority.** All of it is ring state.
- **The host owns every level:** ring terms, garden cells, the river overlay and fine ground.
- **It steps only on the saved clock,** in fixed steps and whole-number arithmetic, from the seed, the weather function and the stored state. Any peer could compute a step, but only the host commits one.
- **Players act through commands** (till, plant, water, spread, fell). A client shows its own action at once and corrects to the host's result.
- **What each player is sent:** the ring terms and the garden texture's changes, which are small and go to everyone; the garden cells within a few hundred kilometres; the fine ground nearby.

**Save size.**

| Part | At its budget | Typical |
|---|---|---|
| Ring terms | A few kilobytes | The same |
| Garden cells | 6.4 MB (200,000 cells) | Well under 1 MB |
| River overlay | Under 1 MB | A few kilobytes |
| Fine ground | 16 MB. A one-hectare farm is 2,500 cells and about 60 KB. | Under 1 MB |
| Garden texture | Rebuilt from the above; not saved | |

A slot's ecological state stays in the tens of megabytes at worst, against test profiles that already run to gigabytes.

**What is not feasible, and is ruled out.**
- **Simulating plants or animals as individuals ring-wide.** The garden is cover and population figures.
- **Fine 2 m state everywhere.** It would be trillions of cells.
- **A garden that changes the weather.** Weather stays a pure function of seed, time and machine state. The garden reads the weather, never the reverse.
- **Rivers that change course or level.** The frozen graph stays frozen. Only ponds move.
- **Remembering every felled tree for ever.** Identity is lost at demotion.
- **Two speeds of time.**

**Parts and sizes.**

| Part | Size | Notes |
|---|---|---|
| Vegetation edit store and plots | M to L | Also needed by planting and felling |
| Soil model, and its baseline from the seed's existing fields | L | Pure C#, headless, with golden runs |
| Crop model and catalogue, in two families | L, plus art | About ten crops. Each needs models at several growth stages. |
| Water overlay | L | Must not touch the frozen river data |
| Garden cells: state, succession, spread, cause tags, the budget | L | Pure C#, whole-number arithmetic, headless |
| Ring terms: megastorm wake, engine weather, the Lamp | M | From state that is already saved |
| Promotion and demotion | M | Conserving totals is the test |
| Presentation near: the scatter density factor, stand age, tile rebuilds | L | Touches T5 and T6. Identity with an empty overlay is the gate. |
| Presentation far: the garden texture through the shared ground include and the far view | L | Touches every terrain shader once, in one include |
| Presentation of the farm: soil states, crops, water tint, a moving pond surface | L | |
| Pests, pollinators, animal figures | M | Figures only |
| Tools and screens: tilling, planting, channels, compost, the two instruments, the farm log | M to L | |
| Reputation hooks and co-op replication | M to L | |

Together this is about two XL systems. It is now the largest new system in the plan, larger than the constructs.

**Risks.**
1. **Scope.** It has doubled. The roadmap splits it in two (GF1 and GF2) so that the farm can be played long before the garden is finished. The garden comes after the ring's machines (decided 2026-11-21).
2. **The look of the world can change for good.** Megastorm damage is realistic (user, 2026-11-21): the country a wall sweeps is levelled and comes back as scrub. A misfire or a contained purge leaves a scar thousands of kilometres long, and a struck expedition leaves a ring of scrub (section 2.14). Windthrown forest is new art.
3. **Presentation cost.** A texture sample in every terrain shader, more tile rebuilds, crop instances and soil states all spend the smaller machine's scarcest resources. Each needs a budget before it is built: proposed, at most 0.1 ms of GPU for the garden texture and no tile rebuild outside the tally.
4. **Identity with an empty overlay.** If a world with no departures is not bit-identical to today's, every vegetation golden moves. This is the first thing to prove.
5. **Determinism across machines.** Whole-number arithmetic and steps that do not depend on order are requirements, not preferences.
6. **Legibility.** The two instruments and the assistant's log are part of the first version.
7. **Art.** Crops at every stage in two families, convincing bare and worked ground, silted water, scrub and young woodland that the tree packs may not cover.
8. **Tuning at speed.** At a season a ring day, a mistake in a rate shows in minutes. That helps testing and punishes players, so the first rates should forgive.

## 4. The two play styles, side by side

| | **Abide** | **Blight** |
|---|---|---|
| **The aim** | To be counted: to become part of the house | To make the ring uncounted: to own pieces of it |
| **At the Count** | Stand in the open, quiet, and be seen | Go dark, or be somewhere it cannot see |
| **Power** | Solar film, small cells, and later sockets the ring leaves live | Generators, stolen cells, taps on its lines |
| **Food and materials** | Forage within tolerance. Salvage the dead ring. | Take what is nearest, including from live machines |
| **Survival feel** | The full-survival game: fire, shelter, water, weather | The suit game: power, plating, air |
| **Getting into sites** | Doors open as Standing rises. Until then, find the way that is not closed. | Cutter, cores, overrides |
| **Travel** | Raft, glider, on foot, then the Deep Line by invitation | Rover, aircraft, the Deep Line hijacked |
| **Constructs** | Watchers lose interest. Menders can be helped. | Wardens and purgers. Menders are parts. |
| **The ship** | Its orders are increasingly refused | Its orders are followed, and exceeded |
| **A megastorm** | Rare. The ring's buildings open as the wall comes. | Dig, build to a rating, or work inside it. For the Struck it never stops coming. |
| **Progress is measured in** | Standing | Leverage and dark wards |
| **The world over time** | Dead engines relit. Parked storms clear. The Lamp brightens. | Wards go dark. The Lamp dims. Menders work on your damage. |
| **The suit assistant** | Left alone, and later used as an interpreter | Jammed, doubted and intruded on |
| **Risk** | Slow, and exposed to the ship's impatience | Fast, and exposed to everything else |
| **Weapons** | A sidearm that may never be fired | Carbine, coil rifle, charges, hybrids |
| **Fighting** | Little to none. The ring uses no lethal force on a player above Blight (section 3.12). | As much as the player starts, limited by ammunition and by what each kill costs |
| **Endings in reach** | Counted, the quiet door, the keeping | The keys, the quiet door |

**A note on the word.** The destructive style is called blight here, which is the ring's own word for it (section 2.13). Only a player who goes on harming through a final warning ends up struck from the count (section 2.12). Everything short of that can be walked back.

**Abiding is a discipline, not a menu option.** It is active play: working out the rules by experiment, doing repair jobs that are expeditions in themselves, and holding to the rules while hunger, cold and the ship all argue for breaking them. The danger in designing this path is that it becomes "do nothing". The cure is that the ring only gives things to players who do something for it.

**The blight path is a strategy game inside the survival game.** Killing a spire's feed takes its ward dark: no Count, no responders. A dark ward is territory. The ring retakes it with menders and wardens arriving by the Deep Line, unless the player holds it. The player literally spreads, and the purge follows the spread.

**The shared middle is sneaking.** Both styles spend most of their time unseen. A third way to play falls out without extra work: the ghost, who finishes the game still classed as Fauna and never earns or loses anything with the ring.

**Switching.**
- Standing is one number, so a player is never locked into a side. Harm and Regard both always count.
- Coming back from the blight side takes absence and repair. It is always possible short of the endings.
- Going over from the abide side is instant. Trust is slow to build and quick to spend.
- **What persists after a switch:** broken machines stay broken until someone mends them, the Lamp keeps its brightness, and the ring keeps every counter it has learned (section 2.9). The ring forgives; it does not forget.
- **One act cannot be walked back quickly:** breaking rule 6. Interfering with the Lamp drops Standing straight to Blight.

## 5. Story spine and structure

### 5.1 Recommendation

**An open world with a story spine**, as the user leans. Specifically:
- The world is open from the landing. Nothing is walled off. Places are closed by distance, weather, altitude, air and doors, and every door has three keys (section 3.9).
- The spine is the eight discoveries of section 1.8, grouped into three acts.
- **Spine sites are roles, not coordinates.** "The nearest spire", "the nearest offline Wall Engine", "the wall top above it", "the main spire". Every seed already has all of them in its registries, so the story works on any world and never needs hand placement. Only the landing candidates need a finder that guarantees a fair start.
- Three spine beats change the state of the whole ring: the Closing, the ship's second objective and the purge it provokes. These are what make it a story and not a checklist.
- Expeditions are the unit of a session inside the open world.

### 5.2 The three acts

**Act 1: Fauna.** Landing, the sealed start, the first night, the first eclipse seen from the ground. The first spire and the first time the ring looks back. The field log begins to fill. Discoveries 1 to 4.
- *It ends with the Welcome and the Closing.* The ship, impatient, sweeps the hub with radar. The Lamplighter has known for a century that the house can be seen and has had no way to see who was looking. The sweep is the first thing from outside to touch it. It answers first with a welcome (section 1.12): for a few minutes the sites in the ward light and stand open and the pulse runs out of its time. Then it sees who called, and turns the field solid to craft. No more drops, no ascent, and the uplink needs real power. The player sees the ring greet something and then shut the sky, and understands neither until Act 3. The beat is caused by the ship, so it is fair to every play style, and it uses a policy value the field already has.

**Act 2: The long way round.** Home is now on the far side of a door only the ring can open, so the player must go and find out what the ring is. That means the first reactor descent, the first ride on the Deep Line, and a journey of thousands of kilometres to a dead Wall Engine and the wall top above it. Discoveries 5 and 6. The repair-or-strip choice at the engine is the first one the whole ring can see: a storm that has stood for centuries either clears over the following hours, or the player leaves with the best salvage in the game.
- The distances are forced by an existing decision: offline engines are at least ten stations apart and never near the main spire. So the spine is ring-scale by construction, and the Deep Line is on it.
- *It ends with a purge.* The ship reveals the second objective in its charter, to judge the ring for settlement, and acts on it: an automated survey lander is driven through the closed field by force and starts drilling. The ring answers with its last resort. A megastorm forms a thousand kilometres or more away and comes for the lander, and for everything between. When the wall has passed over its cause, it stands down. What it leaves is a levelled country that can be seen on the arch for the rest of the game. Whether every story should contain this purge is question 1 in section 9.8. If a player has already been struck from the count, a purge is already theirs.

**Act 3: The Lamp.** The purge has passed and left its scar across the arch, and the ship is preparing to try again, which would bring another. The player makes for the main spire, by whichever of the three keys they hold. Down to the archive in the main reactor: discovery 7. Up four kilometres to the crown, timed so the player is there as an eclipse begins: discovery 8 and the endings, with 595 beams converging overhead.

**After.** The keeping (section 1.9) is the open-ended game.

### 5.3 Why not the alternatives

| Structure | What is good about it | Why it is not the recommendation | What this plan keeps from it |
|---|---|---|---|
| **Expeditions from orbit** (drop, objective, extract, repeat) | Session-shaped; easy co-op; no base upkeep | It makes crossing the field routine, when the lore says every crossing is felt. It removes isolation, so the Closing means nothing. It spends a continuous, seamless world on a mission menu. | The expedition as the unit of a session, from a foothold on the ground |
| **Pure sandbox** | Cheapest in content. The Count, Standing and repair all work as sandbox systems. | "Understand the ring" needs an authored truth. An escalating AI with no beats has no arc, only a difficulty curve. | The keeping as post-story play; spine sites that are systemic, not scripted |
| **Linear levels** | Tightest pacing | It throws away the project's main asset, which is the place | Nothing |

## 6. The suit assistant

### 6.1 What stands

The existing direction is kept whole: a practical, observant voice with restrained warmth; facts first; honest about uncertainty; no jokes, no invented readings, no filler reassurance. The eclipse spec's decision also stands: it is a suit voice, "not a ring AI".

### 6.2 The reconciliation

The current rule says to avoid "omniscient ring AI, lore claims". A sentient ring AI does not conflict with that rule. It gives the rule a job.

1. **The assistant is not the Lamplighter and knows nothing privileged about it.** It has the suit's sensors, the ship's feed when there is one, and its own records.
2. **It reports behaviour, never intent.** "The door sealed four seconds after the cutter powered up" is in character. "It doesn't want us cutting" is not. It calls the Lamplighter "the resident system" and keeps to what that system has been observed to do.
3. **Lore reaches its voice only as recovered evidence.** It can read out what a record says and say where the record came from. It does not explain the ring.
4. **Its honesty is the player's anchor.** In a world where a far greater intelligence can manipulate information, the one voice that never guesses is worth more than any weapon. The existing rule was written to prevent a cliché. In this design it is the reason the player trusts the suit.
5. **It explains sparingly, and only in its own way** (the standing rule at the top of this document). What the user has chosen to have explained (section 9.6) comes from the assistant as an observation, a count, a reading or a labelled estimate. Each is said once and then lives in the log. It gives no advice, names no rule and uses none of this document's design words.

Suggested replacement wording for the rule, for the user to approve: "The assistant is not the ring's intelligence and has no special knowledge of it. It reports what the resident system has been observed to do. It may point out a pattern it has counted, and may give an estimate if it labels it as one with its confidence. It never states the resident system's intent, motive or history as fact."

### 6.3 What it does in the loops

- **The pulse countdown** (explained; user, 2026-11-21). After it has watched two passes it says so, once: "That light is regular. Three hundred seconds. I can count it down." A small countdown then sits on the HUD and can be dismissed. It says nothing about what the pulse is for. The megastorm and eclipse line tables already exist.
- **The first hour** (explained; user, 2026-11-21). Two restrained things tell the player that power matters. Before the landing, the mission's own protocol page asks for emissions discipline "until we know what is listening", which is the caution a real expedition would write. And after the first time the ring responds to a loud act, the assistant makes one observation, once: "That followed our power draw. I had us at full output when the light passed." Neither says what the ring is or what it wants.
- **The estimate and the district reading** (explained; user, 2026-11-21): sections 2.12 and 2.2.
- **Soil and water tests** (explained; user, 2026-11-21): section 3.13.
- **The field log** (section 2.6): what happened, when and where, with a page of the patterns it has counted. It is its most important screen.
- **Analysis.** The leaf. The air. Salvage.
- **Vitals and warnings**, with critical lines always authored and never rewritten, as the megastorm design already requires.
- **The database**, unlocked by first observation, as "Structural resonance" already is. Entries may draw conclusions (explained; user, 2026-11-21). They are written as the expedition's own working notes: what was observed, then a conclusion marked as the current reading. A conclusion can be wrong, and it is revised when the evidence changes. The Veil's entry begins as "a malfunction" and is corrected twice (section 1.12).

### 6.4 Interference, and the rule that keeps it fair

The user wants the ring to interfere with the suit AI. Done carelessly, that teaches the player to ignore the assistant. Three tiers, each with a tell:

| Tier | From | What happens | How the player knows |
|---|---|---|---|
| **Denial** | Trespasser | Functions are lost near live spires: the uplink, team markers, scan range, the map | The assistant says so at once: "I've lost the ship." |
| **Doubt** | Blight | Sensors are fed noise. Readings widen. | The assistant reports its own confidence, as it always has. Now the numbers are bad. |
| **Intrusion** | Blight, and the Act 3 story | A waypoint that it did not set. A line spoken in its voice that it did not generate. A gap in its records. | See the rule below |

**The fairness rule: the assistant never knowingly lies, and nothing that did not come from it is undetectable.**
- Every statement it makes carries its source. Injected content has none.
- An injected line sounds slightly wrong. The voice prototype already has separate clean, suit-speaker and radio effect chains, so the tell can be made in the audio and not in the words.
- The assistant catches intrusions after the fact and says so plainly: "A line was spoken in my voice forty seconds ago. I did not generate it."
- Edited records show as gaps it cannot account for, not as changed text.

**The turn in Act 3.** The same channel becomes the way the Lamplighter finally speaks. The assistant marks it: "This is not me. Relaying." The player has spent the whole game learning to tell the two voices apart, and that skill is what makes the conversation at the crown trustworthy.

### 6.5 Practical notes

- **Authored lines are the baseline.** The local language-model question-and-answer panel that exists as a debug prototype could become a feature (asking the suit a question in your own words), but nothing in this plan depends on it, and it has a memory and performance cost on the smaller machine. If it ships, record edits map naturally onto its per-world conversation store.
- **In co-op each player has their own assistant.** That is what lets the ring make two of them disagree.
- **The ship's voice** is the second human-language voice and needs the same pipeline decision as the assistant's: prerendered authored lines first.

## 7. How the existing tech maps to gameplay

### 7.1 What is already built, and what it becomes

| System | State today | Gameplay role | The gap |
|---|---|---|---|
| Grid pulse and its clock (`EmitterGridPulseClock`) | Built | The Count | The census model (section 2) |
| Spires and the surface facility (walkable collision, entry portal, gallery) | Built | First threshold sites; the centre of each ward | Doors, sockets, terminals: the site kit (7.3) |
| Reactors, shafts, the lift and the bore drop | Assets built. The underground shape library and baked cavern shells are done. Placement and gameplay are paused. | Deep Line stations; the heart of a ward | The paused reactor milestone (7.5) |
| Main spire and main reactor | Assets and the surface site built | Act 3 | Interior gameplay |
| Eclipse event, with a per-site output seam (`IEclipseSiteOutput`) | Built | The session event; the ring-wide scoreboard | Feeding the seam from story state |
| Wall Engines | Registry and forecourt terrain only. No meshes, and the storm and drought anomalies are not wired into the weather. | Act 2; blind spots; salvage grounds; repair or strip | Grid spec phases C4 and C5, already specified |
| Weather Lung | Site built and placed, with its breathing rig. No weather coupling. | A major optional site near the main spire: where directed weather is made | Later |
| Containment field, crossing ripples, the pass/resist/solid policy, vacuum-side audio | Built. The policy has no gameplay effect yet. | The field sense; the Closing; the wall-top view | Bookkeeping and the policy switch |
| 252 projector towers and the wall-top walk | Built | Discovery 6; the blight endgame's targets | Site kit |
| Megastorm model, hazard queries (overpressure, rain, wake, cover), the structure-rating interface, its PDA lines | Built, with no damage | The purge and the misfire (section 2.14) | Damage, ratings, megastorms that end, a saved record of past sweeps |
| Storms, lightning, storm arrival, the wet world, shelter, rain audio | Built | Inputs to body temperature; cover from the Count; reading the sky | The exposure model |
| Rivers with danger classes, lakes, ocean, swimming, underwater | Built; rivers frozen as generation 2 | Water, routes, cover, the air meter | Flow acting on the player (T9i) |
| Forest and grass | Built, in revamp | Canopy cover, forage, fuel | Forage nodes |
| Surface terrain edits (2 m lattice, persisted) | Built | Pits, berms and trenches: real cover from the megastorm's overpressure and ground lightning. Camp platforms. | Volume digging is not in the default backend (7.5) |
| Day and night, the opposite arch, the far view | Built | Reading the ring from far off: where the megastorm is, which engines are dark | None |
| Minimap and trackable markers | Built | The assistant's map; caches; teammates | Screens |
| PDA announcer, subtitles, line tables, database unlocks | Built with subtitles; the voice pipeline is prototyped | All narrative delivery | The field log; voice |
| Scale-anchor towers | Built | None. To be removed (user, 2026-11-21). | A small removal order (7.5) |
| Save slots, autosave, saved clocks | Built | Carry the game state | A game-state document |
| Canonical ring coordinates and the floating origin | Built | The shared language for co-op (7.4) | None |
| Travel to coordinates; the site teleport sequencer | Built | The Deep Line's mechanism; test tooling | A ride and stations |
| Noclip flight | Built as a development tool | Stays a debug tool | Real flight is separate and late |

### 7.2 What each planned terrain phase enables

| Phase | Enables |
|---|---|
| T2, T3 rivers and lakes (frozen) | Water as a need and as a route. Danger classes as natural gates. Water sound as the cue for danger ahead. |
| T4 ground surface | Readable footing and wetness, which feed movement and body temperature |
| T5 scatter and rocks | Cover and lee shelter at human scale. The scatter foundation is also the natural home for deterministic forage and salvage nodes. |
| T6 vegetation | The canopy that halves a signature. Forage species, fuel, understory to hide in. |
| T7 landforms, cliffs and coves | Landing sites, glide launches, raft harbours, hummock country for shelter from the wall |
| T8 rock faces and new trees | Climbing surfaces; forest variety that helps navigation |
| T9a small streams | Water within a walk almost anywhere, which loosens the water meter in a good way |
| T9c waterfalls and deltas | Landmarks to navigate by |
| T9e sinkholes and caves | Natural blind spots: hideouts and cache sites the Count cannot see into |
| T9g headworks and spill fans at Wall Engines | Ready-made Act 2 places at the foot of each engine |
| T9i flow, danger and floating | Rafts, drift salvage, rivers that can kill. The quiet travel mode depends on this group. |
| Vastness V3 to V6 | No mechanics, but the wall-shadow tide and the eclipse edge are how a player far from a clock tells the time |

The "new worlds only" rule applies as usual. Anything that changes what a seed generates (landing candidates, salvage sites) is a step on the generation ladder. Game state is not.

### 7.3 The new systems, with rough sizes

Sizes are in the repo's work orders: S is 1 to 3, M is 4 to 8, L is 9 to 20, and XL is more than 20 with its own design doc.

| System | Size | Notes and risk |
|---|---|---|
| Game-state core and save | M | A new document per slot: inventory, Standing, wards, story flags. Low risk. |
| Player state: meters, conditions, exposure | M | Reads existing weather, wetness, water and altitude queries. The risk is the 3,300-line explorer controller it must sit beside. |
| Interaction, items, inventory, containers | L | Ordinary work, a lot of it |
| HUD and the assistant's screens | L | Vitals, map, field log, database, inventory |
| Signature and senses | M | Pure model. Reads the pulse clock, the field bus, cover queries. |
| The director: Attention, Standing and the ward ledgers (reputation, section 2.12), response scheduling, the tactic ledger | L | Pure C#, headless-testable, ticked on the weather clock. The design risk is tuning, not code. |
| Vegetation edit store: felling, planting, plots, the dawn tally | M to L | New. Reputation's garden half depends on it (section 2.12). It must sit on the T5 and T6 scatter without disturbing their determinism. |
| The living garden: the farm's soil, crop and water model, the ring-wide garden cells and ring terms, promotion and demotion, presentation near and far | About two XL | Section 3.13, milestones GF1 and GF2. Now the largest new system in the plan, with its own design doc. The risks are scope, presentation cost and bit-identity with an empty overlay. |
| Site kit: doors, sockets, terminals, lifts, power state | L, then M per kind of site | The spire facility first. Interiors are the real content cost. |
| Directed weather | M | Storms the director can place, steer and save. Must stay a pure function of seed, time and saved events (7.5). |
| Megastorm damage and shelter ratings | M | The queries and interfaces exist. Also megastorms that end by themselves, a misfire schedule and a saved record of past sweeps (section 2.14). |
| Constructs | XL | Four kinds: art, animation, behaviour, damage both ways. The largest content risk in the plan. |
| Weapons, tools and combat | L to XL | Firearms are in (section 3.12): ballistics, ammunition, hit reactions and damage on constructs, and first-person weapon art and animation, which the project does not have |
| Fabrication and recipes | M | About thirty recipes |
| Glide wing | S to M | A movement mode, not a vehicle |
| Raft and boat | M | After T9i; the water system has no rigid-body buoyancy today |
| Rover | L | A physics vehicle on a streaming collision window |
| Aircraft | L | Late |
| The Deep Line | M | After the reactor underground |
| Wall Engine presentation and anomaly weather | L | Already specified |
| Co-op networking | XL | Section 7.4 |
| Voice | M plus content | Assistant, ship and, late, the Lamplighter |
| Spine content: sites, beats, endings | XL | Content, not systems |
| Animals | XL | Optional and late |

### 7.4 Co-op networking: the architecture decision

**What the project has in its favour.**
- **The world is a function of its seed.** Terrain, weather, the pulse, the eclipse and the megastorm are all computed from the seed and two saved clocks. Peers need the seed, the clocks and a small amount of state. No terrain crosses the wire.
- **There is already a shared coordinate language.** Canonical double-precision ring coordinates exist. Each client can keep its own floating origin and convert.
- **The project already builds pure models with thin nodes.** That is the shape networked state needs.
- **There is a precedent in the tree:** the retained voxel tools have revisioned edit commands and replication snapshots.

**What it has against it.**
- **Everything streams around one observer.** The 768 m terrain window, collision, vegetation tiles and the near cloud sculpt all follow one player. A host cannot simulate physics around a second player 500 km away without a second streaming focus, and adding one touches nearly every system in the project.
- **Godot's scene replication assumes shared coordinates.** With an origin per client, the built-in transform sync cannot be used as it comes.
- **Weather consumers reset on a clock jump of more than 5 seconds,** so clocks must be slewed, never snapped.
- **The explorer controller mixes input, physics and test hooks** in one class.
- **Pause** cannot stop the world in co-op.

**Options.**

| Option | How it works | Verdict |
|---|---|---|
| A. Host simulates everything | The host runs physics and AI around every player | Needs multi-focus streaming. Rejected: it is a second engine project. |
| **B. Distributed authority** | Each client is the authority for its own player and for the things near it. The host is the authority for ring state: the clocks, the director, Standing, story flags, containers, the terrain edit journal, and which client owns which construct. Everything is exchanged in canonical coordinates. | **Chosen (user, 2026-11-21).** It avoids multi-focus streaming entirely. It is the model seeded co-op survival games have used successfully. Its cost is trust, which is acceptable for two to four friends. |
| C. Dedicated server | A headless authority | Not needed for this audience; revisit only if the game outgrows friends-only play |

**Rules to adopt from the first line of gameplay code,** so that co-op is an addition and not a rewrite:
1. Gameplay state lives in pure models keyed by canonical coordinates and the saved clocks.
2. State changes only through serialisable commands applied by the authority.
3. No gameplay code asks for "the player" or "the camera". It is given a player.
4. The director ticks in fixed steps on the weather clock.
5. Nothing in gameplay depends on frame rate or on what happens to be rendered.
6. The controller is split into input, intent and motion, so a remote player can be driven by intent.

**Timing.** Decide now, spike early, build in the middle. A two-instance spike with no gameplay proves the architecture while the slice is being built. Full co-op comes after the expedition loop and before the constructs, because enemies are the most expensive thing to retrofit for a network and the cheapest to build on one.

**Risks that remain under option B.**
- Collision heights must match bit for bit on every peer. The generation fingerprint tests are the start of that proof; it needs a two-machine check.
- Ownership handoff when two players share a ward.
- A remote player needs a body: a third-person model and animation the game does not have.
- Two Godot processes on one GPU for tests, under the existing queue.
- Host migration is out of scope for a first version.

### 7.5 Other technical risks

1. **The underground.** The default Certified backend is a height surface with no volume: no caves, no block building, digging only changes height. The reactor milestone was paused in October waiting for a voxel backend decision. This plan needs authored underground (reactor caverns, Deep Line stations, cave hideouts) and does not need dig-anywhere volume. The paused work's baked-shell approach delivers the first without the second. That is question 7. **Decided 2026-11-21: authored underground only.**
2. **Constructs on a streaming, curved world.** Ground navigation there is hard. Three of the four constructs fly; the fourth walks authored routes inside sites.
3. **Interiors.** The assets are architecture at kilometre scale. Rooms a person can use are mostly still to be made, and that, not systems, is where Acts 2 and 3 will cost the most.
4. **Performance.** The smaller machine is GPU-bound at 40 to 44 ms with a scarce main thread. Proposed budgets: the director at most 0.1 ms per frame on the main thread, constructs at most 8 draw calls in view, the HUD at most 0.3 ms.
5. **Weather purity.** Today's weather is a pure function of seed and time, which is why saves and rewinds are exact. Directed weather must keep that by being a function of seed, time and a saved list of director events, in the way the megastorm's saved anchors already work.
6. **Engine direction.** Decided 2026-11-21: Godot stays. `docs/unreal-rewrite-master-plan.md` (2026-10-23) still describes the Godot project as frozen and should carry a note that it is dormant.
7. **Menu convention.** Gameplay gets a tab in the grave-key menu for the values a player would really tune (survival rates, difficulty). The director's internals and all A/B switches belong in debug tooling.
8. **Tower removal (a small work order).** The user decided on 2026-11-21 to remove the scale-anchor towers everywhere, including existing saves. It is not a step on the generation ladder. The order: take the `ScaleAnchors` node and `RingScaleAnchorLayer` out of the gameplay scene for every world, with their shader, tests and manifest entries; check that nothing saved or shown (a save field, a minimap marker) still refers to them; update the README line about landmark towers; and close vastness work order V2-4 (tower livery) and the contingent V7b (chain lines of masts), which no longer have a subject.
9. **Machine state (amends a binding decision).** Decision 3 of 2026-10-16 in `docs/emitter-grid-climate-machines-spec.md` fixes failures by the world seed "with no dynamic failure or repair". The user decided on 2026-11-21 that players may repair and sabotage the ring's machines. This plan does not edit the spec; it needs a note there. The intended shape:
   - the seed still fixes the starting state, so a fresh world is unchanged;
   - each machine's state after that is saved story state;
   - an engine's storm or drought fades in or out over hours, so no weather consumer sees a jump;
   - spire state feeds the eclipse event's existing per-site output.

## 8. Roadmap

Each milestone ends in something the user can play. Play tests come back as a list with coordinates and pass criteria, as usual.

### G0. Seams

- **Goal.** Places for gameplay to live, with no gameplay yet: the game-state document, the player-state component beside the controller, an interaction ray, a HUD shell, and the six co-op-ready rules of 7.4 written down as a contract. Lead design docs for the signature model, the director and the site kit. The tower removal order (7.5) can ride along here.
- **Playable test.** Pick up an object, put it in a container, save, reload, and find it there. A debug overlay shows what the weather is doing to the player: temperature, wetness, wind, altitude, shelter.
- **Unblocks.** Everything.
- **Size.** M to L. No dependencies.

### N0. Network spike (alongside G1)

- **Goal.** Prove distributed authority before anything is built on it.
- **Playable test.** Two instances on one machine with one seed. Each sees the other's marker in the right place, across an origin recentre and from 1,000 km apart. Both see the same pulse pass at the same moment. No gameplay.
- **Unblocks.** The co-op decision, with evidence.
- **Size.** M.

### G1. First Count (the vertical slice)

- **Goal.** The smallest loop that proves the fantasy: being ignored by something vast, then noticed by it, then learning how to live with it.
- **The place.** One regular spire and about 5 km around it, on a current test world, with forest and water.
- **In.**
  - A lander as the foothold: recharge, storage, save.
  - Suit power, body temperature, water, and health with falls.
  - About eight items and a simple inventory.
  - The Count: going dark, standing to be counted, the assistant's countdown, cover from canopy, rock and water.
  - Attention, and the ladder from Uncounted to Trespasser and to Stray.
  - One response on each channel: a watcher, doors and lights at the spire facility, a rain cell that may or may not be coincidence.
  - The field log with its patterns page, the district reading, and three rules to be learned: the Count, closed doors, powered tools near structures.
  - About forty assistant lines, subtitled.
  - One job: fetch a written-off part from the spire's gallery to repair the lander's antenna and raise the ship.
- **Out.** Co-op, weapons and combat, crafting, the underground, the megastorm, the Deep Line, the story.
- **Playable test.** About an hour, unprompted. It passes if the player:
  1. changes what they are doing because the pulse is coming, without being told twice;
  2. can state two of the three rules afterwards in their own words;
  3. is noticed at least once and brings the ward back down at least once;
  4. describes the ring as indifferent at first and attentive later.
- **What this test also checks.** That the things the user chose to have explained (section 9.6) read as the suit talking and not as a tutorial. If a play tester describes any of them as the game telling them what to do, that line is rewritten.
- **Unblocks.** The decision to build the rest. If the Count is not fun here, nothing built on it will be.
- **Size.** XL across several orders, roughly 30 to 40. Depends on G0.

### G2. Expedition

- **Goal.** The session loop holds up over several sessions.
- **In.** All the meters and the sealed start. The fabricator and about fifteen recipes. The first dead-ring salvage site. Camps and caches. The glide wing. Forage with ward tolerance (rules 1 and 3). The choice of landing site. Rafts, if T9i has landed.
- **Playable test.** Over three sessions, establish a second camp at least 30 km from the lander and bring back one part that cannot be fabricated.
- **Unblocks.** Content at scale; a game worth playing with someone else.
- **Size.** XL. Depends on G1; benefits from T5, T6 and T9i.

### GF1. The living ground (the farm)

- **Goal.** Farming as a full soil, crop and water model (section 3.13), fast enough to show results in one session, read by the ring's reputation.
- **How it is staged.**
  - The design doc for all of section 3.13 is written alongside G0.
  - The models are pure code. They are built and tested headless while G1 and G2 are in progress, without touching gameplay.
  - It becomes playable after G2, which supplies the foothold, the inventory, the fabricator and the vegetation edit store.
  - It runs alongside G3 and G4, not ahead of them.
- **In.** The fine model, both crop families, the water overlay, the two instruments, the farm's presentation.
- **Playable test.** One session of about two ring days: two plots on one slope above a stream, one farmed with care and one for yield with fabricated fertiliser. It passes if:
  1. both plots give a harvest inside the session;
  2. the soil tests diverge in the way a farmer would expect;
  3. at the first rain the hard-farmed plot shows rills and the stream below it runs brown, and the other does not;
  4. the ring's treatment of the player in that district changes at the next dawn, and the assistant's estimate says so;
  5. resting and covering the damaged plot turns its readings round by the end of the session;
  6. a second player who joins midway sees the same two plots in the same state.
- **Unblocks.** The abide path as a way to live, not only a way to behave.
- **Size.** XL. Depends on G2, the rivers freeze, T4 (ground), and T5 and T6 (scatter); on T7-11 for ponds and wetlands; and on T9a if small streams are to be farmed from.

### GF2. The living garden (ring-wide)

- **Goal.** The whole garden as an ecosystem that answers to what is done to it: garden cells, ring terms, promotion and demotion, and the overlay that shows it near and far.
- **How it is staged.**
  - **First, and early: identity.** The scatter density factor and the garden texture go in with an empty overlay and are proved bit-identical to today's world. This can be done as soon as T5 and T6 allow, well before the rest.
  - **Then the model,** headless: cells, succession, spread, cause, the budget.
  - **Then the ring terms:** the megastorm's wake and the engines' weather.
  - **Then it is played, after G4** (decided 2026-11-21: after the ring's machines, with the bit-identity proof early).
- **Playable test.**
  1. A world with no departures matches its vegetation and terrain captures from before the feature, exactly.
  2. Fell a clearing 300 m across and walk away. A ring day later it is grass, and from a ridge it reads as a pale patch. Eight ring days later it is scrub.
  3. Set a drought engine to repaired and watch from the far arch: over about twenty ring days its patch greens.
  4. Let a megastorm sweep exposed forest. The country behind it is levelled, with trees standing only in deep shelter, and eight ring days later it is scrub.
  5. A second player 500 km away sees the same garden on the arch.
  6. The garden's step stays inside its worker budget, the garden texture inside 0.1 ms of GPU on the smaller machine, and the save inside the sizes of section 3.13.
- **Unblocks.** The Fever's cost made visible. Repair that changes the look of the ring. The keeping as a real occupation.
- **Size.** XL. Depends on GF1, T5 and T6, and the megastorm and machine state that G4 and G5 bring.

### G3. Company (co-op)

- **Goal.** Two to four players through everything in G1 and G2.
- **Playable test.** Two players split a ward: one draws the watcher while the other goes in. Both come home. The host's save holds it.
- **Unblocks.** Constructs and combat built for a network from the start.
- **Size.** XL. Depends on N0 and G2.

### G4. Immune response

- **Goal.** The whole ladder, in both directions.
- **In.** Wardens, menders and purgers. Firearms, tools and combat (section 3.12). Directed weather. The tactic ledger. Assistant interference, tiers one and two. Megastorm damage and shelter ratings, with the megastorm started from the debug menu. Dark wards.
- **Playable test.** One save played twice, once abiding and once as a blight. Both get inside the spire. The blight run takes a ward dark and holds it for a ring day. The abiding run reaches Guest.
- **Unblocks.** The story, which needs every rung.
- **Size.** XL. Depends on G3.

### G5. The Closing and the long way round (Acts 1 and 2)

- **Goal.** The spine from landing to the purge.
- **In.** The reactor underground and the Deep Line. Wall Engine presentation and anomaly weather. Repair or strip. The wall top. The ship's voice. The Closing. The purge and its end. The misfire schedule.
- **Playable test.** Play from landing to the megastorm forming on the arch.
- **Unblocks.** Act 3.
- **Size.** XL. Depends on G4, on the paused reactor milestone and on the Wall Engine work in the grid spec.

### G6. The Lamp (Act 3 and the endings)

- **Goal.** The end of the story.
- **In.** Main spire and main reactor gameplay. The archive. The crown during an eclipse. The Lamplighter's speech. Interference tier three. The five endings.
- **Playable test.** Each ending reached from one late save.
- **Size.** XL. Depends on G5.

### G7. The keeping

- **Goal.** The game after the story, and the larger toys.
- **In, each on its own go.** The maintenance loop. Rover and aircraft. Animals. Block building, if the backend allows. More constructs and counters.

**The order, and why.** Seams, then the slice, because the slice is the cheapest way to find out whether the Count is fun. Then the expedition loop, because co-op needs something to do. Then co-op before enemies, for the reason in 7.4. Then the ladder, then the story, because the story needs the ladder. The living garden runs beside this line in two parts. The farm (GF1) is played once the expedition loop exists. The ring-wide garden (GF2) proves its bit-identity early and is played after the ladder, because its two largest drivers, the megastorm and the state of the machines, arrive with G4 and G5.

## 9. User decisions (2026-11-21) and new questions

### 9.1 Decisions

| # | Question | The user's answer | Where it lands in this plan |
|---|---|---|---|
| 1 | The core truth | **Adopted, with one change:** the false sun is a working light that keeps the ring lit in total darkness, not a beacon. | Sections 0, 1.1 and 1.2 rewritten. The vigil, the three layers and the four endings stand. |
| 2 | Where the ring is | **Another star.** In the user's words: "maybe the ring cloaking system malfunctions and humans discover the false sun meant to keep the ring lit when it was in total darkness was picked up by our instrument on earth." | Section 1.2: a sun-like neighbour star, the Veil and why it failed, the detection, and the check against the eclipse spec. Section 1.7: the long crossing and the ship as the only human authority. Sections 1.8, 1.9 and 5.2: discovery 6, the endings and the Closing reworked. |
| 3 | May players change machine state? | **Yes:** repair and sabotage are both allowed. | This amends decision 3 of 2026-10-16 in the grid spec. Noted in 7.5 item 9; the spec itself is not edited here. |
| 4 | Combat | **Firearms are allowed,** slightly futuristic, with limited ammunition at the start. | New section 3.12. Also 2.3, 2.8, 2.9, 3.6, section 4, 7.3 and the roadmap. |
| 5 | Survival and death | **The blend, with three presets.** Death returns the player to the foothold with gear left where they fell. | Sections 3.5 and 3.11 marked decided. |
| 6 | Co-op and engine | **As recommended:** friends-only distributed authority, spiked alongside the slice. **Godot stays.** | Section 7.4 marked chosen; 7.5 item 6. |
| 7 | Underground | **Authored only.** | Section 7.5 item 1. The paused reactor milestone can resume on its baked shells. |
| 8 | Scale-anchor towers | **Remove them.** They do not become census masts. | Section 2.2 re-homes the Attention readout on the suit, the spires and the ring's behaviour. The removal order is 7.5 item 8. |

### 9.2 Second round (2026-11-21)

The four questions raised by the first answers, with the user's replies.

| # | Question | The user's answer | Where it lands |
|---|---|---|---|
| 1 | Does the Lamplighter know the Veil has failed? | **It knows.** It was not a side effect it never noticed. Why it knows, and why the Veil is still down, is a mystery for the player to uncover. | New section 1.12, with three candidate reasons. Sections 0, 1.2, 1.4, 1.5, 1.8, 1.9 and 5.2 reworked. The reason was chosen in the third round (9.3). |
| 2 | What may be shot? | **Constructs, and animals if animals ship. No human enemies. Friendly fire off by default.** | Section 3.12 |
| 3 | How large is the mission? | **A small science crew with a sleeping reserve, and the settlement clause in its charter.** | Section 1.7 |
| 4 | Where does the Veil live, and may the player mend it? | **On the outer blades of the projector towers, and the player can mend it** (the quiet door ending). | Sections 1.9 and 1.12; the working names table |
| - | The towers, again | **Remove them everywhere, including existing saves.** | Section 7.5 item 8 |
| - | Reputation | **Add a reputation mechanic** with good, neutral and bad acts. | New section 2.12 |

### 9.3 Third round (2026-11-21)

| # | Question | The user's answer | Where it lands |
|---|---|---|---|
| 1 | Why is the Veil still down? | **Reason A, with C as the belief the player holds first.** | Section 1.12 marked decided; 1.5, 1.9 and 5.2 no longer hedge |
| 2 | A ledger for each ward? | **Yes.** | Section 2.12, "Two levels" |
| 3 | What is harmful farming? | "Farming in ways that hurt the environment, same as today; we can iterate on this and what would make sense for the story." | Section 2.12: the garden ethic and the act table reworked around sustainable and harmful practice, with the story reason a ring cares. The ship's crops are no longer treated as foreign. |
| 4 | The bottom of the ladder | **Irredeemable, but hard to reach.** Every rung above it is recoverable. | Section 2.12, "The bottom of the ladder": the last count, the warnings, the point of no return, co-op. Sections 2.5, 2.7 and 2.10 updated. The megastorm is now a state of the world, separate from the ladder. |
| 5 | Naming | **"Virus" is shorthand only; the game needs an in-world term.** | New section 2.13, with proposals |
| 6 | Co-op: do a joining friend's acts count? | Not answered. | Carried to 9.4, question 3 |

### 9.4 Fourth round (2026-11-21)

| # | Question | The user's answer | Where it lands |
|---|---|---|---|
| 1 | The names of the ladder | **Adopt all five:** Counted, Guest, Stray, Blight, Struck. | Renamed throughout sections 0 to 8; section 2.13. "Tolerated", "Infection" and the old shorthand for the destructive style are retired; one line in the names table of section 0 keeps the note. Rows above this one in section 9 keep the old words as a record. |
| 2 | Co-op | **Being struck is personal. Ordinary acts count for the expedition, with a host option to confine a friend's acts to their own trace.** | Section 2.12 |
| 3 | Can a struck player get home? | **Yes, by force:** tearing down the field at the wall top. The bleakest ending. | Section 1.9, "The breach"; section 2.12 |
| 4 | How deep is farming at first? | **Deep from the start,** against the recommendation: a full soil, water and ecosystem simulation in the first version. | New section 3.13; new milestone GF in section 8; section 7.3 |

**Assumed, pending the user.** Two recommendations were never answered and the plan proceeds on them:
- reputation is shown without a raw number (section 2.12);
- a Guest must first undo their own damage to the ring's machines (section 2.12).

### 9.5 Fifth round: the scope of farming (2026-11-21)

| # | Question | The user's answer | Where it lands |
|---|---|---|---|
| 1 | How fast does the living ground run? | **Faster than the ten field days a ring day that was proposed.** Results should show within a single session. | Section 3.13, "Time": proposed as one growing season a ring day, with the farm stepping at every pulse, on one clock for the whole ring. A two-speed scheme was considered and rejected. Confirmation is question 1 in 9.7. |
| 2 | How far does the simulation reach? | **The whole garden,** as a ring-wide ecosystem. | Section 3.13 redesigned: the baseline as the balance, four levels of detail, promotion and demotion, the overlay near and far, authority, save size and what is ruled out. Milestone GF is split into GF1 and GF2. |
| 3 | What is farmed? | **Both** the garden's wild food plants and the ship's cultivars. | Section 3.13, "What is farmed" |

### 9.6 Explain to the player? (answered 2026-11-21)

The user's standing rule: "Try not to over explain things to the player, a lot of this will come in as lore. Ask me if something should be explained to the player." Asked about the nine marked items, the user chose to explain eight and hide one. Each explained item comes from the assistant's voice or a suit instrument, once and briefly, and never as tutorial text.

| # | Item | The user's answer | How it is presented | Where |
|---|---|---|---|---|
| 1 | The final warning before Struck | **Explain** | Two authored spoken lines from the assistant: its reading at the start of the last count, and one sentence as the player begins the act | 2.12 |
| 2 | A join-screen warning for a struck expedition | **Keep hidden** | No warning. A friend finds out the way the others did. | 2.12 |
| 3 | Soil and water tests | **Explain** | Two instruments, a soil probe and a water test, giving readings with numbers and units. The assistant reads and logs them and gives no advice. | 3.13 |
| 4 | The assistant pointing out patterns in its log | **Explain** | After three repeats it says so once, as a count and not a rule. The pattern then sits on a page of the log with a running tally. | 2.6 |
| 5 | A worded estimate of how the ring is treating the expedition | **Explain** | One line in the assistant's own words with its confidence, spoken when it changes and kept on a suit page. Not a meter. | 2.12 |
| 6 | A district activity reading on the HUD | **Explain** | A small three-step instrument (quiet, stirring, searching) built from what the suit observes, which dims to "no data" far from any site | 2.2 |
| 7 | Something in the first hour about powered suit use | **Explain** | The mission's protocol page asks for emissions discipline; and the assistant makes one observation after the ring first responds to a loud act | 6.3 |
| 8 | Database entries that draw conclusions | **Explain** | Entries are the expedition's working notes: an observation, then a conclusion marked as the current reading, revised when the evidence changes | 6.3 |
| 9 | The pulse countdown, unprompted | **Explain** | After two passes the assistant offers it once. A small countdown then sits on the HUD and can be dismissed. | 6.3 |

Unchanged: light colours and the ring's signals have no legend, and the ship's explanations are a character's opinions and are sometimes wrong.

### 9.7 Sixth round (2026-11-21)

| # | Question | The user's answer | Where it lands |
|---|---|---|---|
| 1 | One growing season a ring day, on one clock? | **Yes.** | Section 3.13, "Time", marked decided |
| 2 | How hard does the megastorm hit the garden? | "Since the megastorm is reserved for a purge, or a glitch, destruction will be realistic." **Full realistic damage. A megastorm happens only as a purge or as a glitch.** This went against the softer recommendation. | New section 2.14: the purge and the misfire, how a megastorm is made, how rare a misfire is and how a player can cause, prevent or stop one, what a sweep leaves, the late game, and what changes against the megastorm as built. Sections 0, 1.1, 2.5, 2.7, 2.10, 2.12, 3.1, 3.4, 3.8, 3.13, 4, 5.2 and the roadmap updated. The megastorm is no longer one of the clocks. |
| 3 | Where does the ring-wide garden sit? | **After the ring's machines (the constructs), with the bit-identity proof early.** | Milestone GF2 |

The megastorm design doc's opening premise ("an ever-present threat once it is triggered") is now out of date and needs a dated note. This plan does not edit that doc.

### 9.8 Lore and loop: the questions that would shape it most

The user, 2026-11-21: "I want to flesh out the lore and gameplay loop first." These are the eight places where the plan is thinnest or where one decision would change the most. Each has options and a recommendation.

**1. Does every story contain a megastorm?**
The megastorm is the project's largest spectacle, and it is now reserved for a purge or a misfire.
- **A. The ship provokes one.** At the end of Act 2 its forced lander brings a purge that ends when the lander is gone. Every player lives through one wall and carries its scar on the arch through Act 3.
- **B. Only the Struck are purged.** Anyone else sees misfires, far off on the arch, and may never stand under a wall.
- **C. A misfire as the showpiece.** One is certain to cross the player's country early in Act 2, as the moment they learn the ring is failing. Purges stay for the Struck.
- **Recommendation: C.** It keeps the user's rule that a purge is something a player earns. It puts the wall in every story without making the ship the cause. And it makes "it is failing" (discovery 5) the most frightening lesson in the game instead of a quiet one. Act 2 then needs a different ending, and the ship's forced lander can simply be destroyed by the ring's ordinary means.

**2. How does the Lamplighter speak, and to whom?**
It is wordless for most of the game. The question is what happens at the end.
- **A. In borrowed words, to the player.** It has listened to the expedition's radio. Its vocabulary is small, exact and domestic, and it counts things.
- **B. Never in words.** Light, tone, doors and weather to the last. The endings are read, not heard.
- **C. To the suit assistant first.** The ring has not met another mind in thirty thousand years. It addresses the suit before it addresses the person in it, and the player listens to two caretakers talk: "This is not me. Relaying."
- **D. In the builders' language, never translated.** The player learns a couple of dozen signs across the game and understands the last scene with them.
- **Recommendation: C, in the words of A.** It gives the suit assistant a place in the story instead of leaving it a narrator. It makes the Lamplighter's loneliness something the player witnesses and is not told. And it is the best character moment the plan has available.

**3. What trace did the builders leave of themselves?**
The plan shows no bodies, images or voices.
- **A. Nothing but their works.** As now.
- **B. Their measure.** Doors, steps, handholds and seats that fit a body that is not ours. One room at the main spire that was plainly made to be lived in and never was. The player infers them from the shape of the space they left.
- **C. Their records.** Images or writing in the archive.
- **D. One of them.** A single builder who stayed, long dead, somewhere the player can find.
- **Recommendation: B.** It keeps them unseen and still gives them a body and a tenderness. It costs set dressing and no characters. An empty room laid for guests says more about the Lamplighter's vigil than any record could.

**4. What is the first hour?**
- **A. Night, sealed, silent.** The lander's hatch opens on a forest at night. The helmet is sealed and the world is muffled. The pulse crosses the sky. The first task is an air sample. The world's clock is set so the first eclipse falls inside the first session, and the Lamp is the first wonder.
- **B. A hard landing.** Gear scattered, the lander damaged, survival from the first minute.
- **C. The long approach.** The ring from orbit and the descent through the field, played or watched, before any boots are down.
- **Recommendation: A.** It is quiet, which the ring should be. It teaches the sealed suit, the Count and the Lamp by letting them happen. B is the genre's habit and makes the ring an enemy at once. C is the most beautiful and the most expensive, and can be added later as a short opening.

**5. What is the player's work in an ordinary session?**
The spine is hundreds of hours wide. Something has to fill a Tuesday evening.
- **A. The surveyor's commission.** The ship asks for things by radio: a sample, a measurement, a place mapped. It pays in drops while there are drops and in standing with the ship afterwards.
- **B. The ring's needs, seen and never asked.** A mender stalled at a broken span. A channel silted. A stand dying at a drought's edge. The player notices and chooses.
- **C. Only the player's own needs and curiosity.** No one asks for anything.
- **Recommendation: A and B together, with no quest log.** Two sources of work that pull opposite ways are the story's argument turned into a routine. The ship asks aloud and the ring never asks at all, which is the right difference between them.

**6. What lives in the garden?**
The cuttings are thirty thousand years old.
- **A. What Earth lost.** Ice-age animals: the great grazers and what hunted them. The ring holds a living world that ours no longer has.
- **B. The small and the quiet.** Birds, fish, insects, a deer-sized grazer. Enough for the census to mean something.
- **C. Nothing that walks.** A garden of plants, birdsong with no birds to find. Eerie, and cheapest.
- **Recommendation: B first, built so that A can arrive.** Start with the small animals, and keep the great ones for remote country and a later release. A herd of mammoth on the far arch through a long lens is a reveal worth holding back. It is also the largest art cost in this list.

**7. What is at the centre, and what is the far side of the sky?**
The opposite side of the ring is always overhead, and the hub is where the Lamp is lit.
- **A. The centre is empty.** Nothing is built at the hub. The Lamp is lit in open space where the beams meet. The far side is first a map the player reads and, late, a place the Deep Line reaches.
- **B. A lamp house.** A structure at the hub, and the last scene is set inside it as the beams arrive.
- **C. The watch is at the hub.** The post that faces the road sits at the centre, and the walls are only engines.
- **Recommendation: A.** The centre of the house being empty is the right image for this story. It needs no new place. And it keeps the finale at the main spire's crown, which exists. The walls then hold everything else the house has: the engines inside them, and the Veil and the watch on top.

**8. Who speaks for humanity?**
- **A. One commander.** A decent person under old orders, who changes as the story goes.
- **B. Two voices that disagree.** A commander and a scientist, and the player overhears the argument.
- **C. No one awake.** The crew are all asleep but the surface team. The ship is run by its mission system, and its orders come as recordings made by people who have been dead for a lifetime.
- **Recommendation: C.** It makes the ship the Lamplighter's mirror: two caretakers carrying out the wishes of people who are gone, with the only living people in the story standing between them. It explains why the ship does foolish things on schedule. It needs the fewest performances. Its cost is warmth: there is no living human to talk to but the other players, which may be exactly right, or too lonely.

**Smaller choices that can wait:** how often eclipses come; whether the Lamp's late ignition and violent collapse are read as signs of age (section 1.2); whether the question-and-answer assistant becomes a feature; a Pleistocene animal roster; and the ship's casting.

**Next step.** The user wants the lore and the loop fleshed out before any build work (2026-11-21). The next step is the answers to section 9.8. G0 (four lead design docs, the co-op-ready contract and the tower removal order) waits for them.
