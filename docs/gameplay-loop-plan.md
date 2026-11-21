# Gameplay loop and core lore plan

Status: proposal, 2026-11-21, revised the same day after the user answered the eight questions. Written by the Fable role (`claude-fable-5-2`) at the user's request, for the user as creative director. Design and planning only: no code, shader, scene or setting was changed. Section 9 records the user's decisions and four new questions. Everything else is still a proposal except where section 9 says it is decided.

The user's premise: "Humanity finding this ring in the near future, you are tasked with landing on and exploring this ring. Turns out the ring has a sentient AI running all of its systems. And right now, you are an intruder."

How to read this: section 0 is the whole plan on one page. Section 1 is the lore. Section 2 turns the ring AI into a system that can be built. Sections 3 to 6 are the loops, the two play styles, the structure and the suit assistant. Section 7 maps all of it onto the engine as it stands, section 8 is the roadmap and section 9 holds the eight decisions that are the user's. Every name marked "working" is a placeholder for the user to replace.

## 0. Summary

**The fantasy.** You are a small warm animal inside a machine the size of a continent, and the machine is beginning to look at you. What it does next depends on what you do.

**The core truth (adopted 2026-11-21, with the lamp reworked).** The ring is a house kept ready for owners who are never coming back, and it is at another star. Its builders stocked it with cuttings of Earth, the last living world on their road, tens of thousands of years ago. They hid it under a veil and left a caretaker mind to keep it. The giant planet it circles puts the whole ring in total darkness for a full ring day every eight hours, so the caretaker lights a small sun at the hub to keep the garden lit. The veil has failed, an instrument on Earth caught that light, and the caretaker does not know the house has been seen.

**The ring AI (working name: the Lamplighter).** It is not hostile. It is old, faithful, short of everything and unable to tell yet whether you are a guest, an animal or an infection. It senses through the ring's own structure, not through cameras:
- the five-minute grid pulse is its census (the Count);
- the ring floor carries vibration to it;
- the containment field feels every crossing;
- its buildings watch their own thresholds.

It files you under a class and responds in proportion. Its last resort, the megastorm, harms the garden it exists to protect, so it is a fever, not a weapon.

**The link between survival and stealth.** Suit power keeps you alive and is also what the Count sees. Every comfort is a signature. That one rule joins the two halves of the feel the user asked for.

**Two play styles, one toolkit.** To abide is to become counted: learn the house rules, mend what is broken, and doors open. To play as a virus is to make parts of the ring uncounted: force doors, strip machines, take districts dark and hold them. Sneaking is the shared middle that both use. Players can switch, and the ring forgives slowly but does not forget.

**Structure.** An open world with a story spine, as the user leans. The spine is eight discoveries tied to kinds of place that every seed already has (a spire, a reactor, an offline Wall Engine, the wall top, the main spire), not to hand-placed coordinates. Expeditions are the natural unit of a session inside that world, not a menu.

**Four clocks that already run in the engine carry the pacing:** the pulse (5 minutes), the day (75 minutes), the eclipse (7.8 hours) and, once the story triggers it, the megastorm lap (25 hours of play).

**First build.** A vertical slice called First Count: one spire, one lander, one drone, three rules and about an hour of play. It needs no co-op, no combat, no crafting tree and no underground.

**The three largest risks.** Co-op networking on a floating-origin world built around one observer (section 7.4). The constructs, which are the heaviest new content. The underground, where reactor work is paused on an open backend decision (section 7.5).

### Working names

| Working name | What it is | Built on |
|---|---|---|
| The Lamplighter | The ring AI, as the expedition crew call it. The mission's formal term is "the resident system". | new |
| The Expected | The builders, in the Lamplighter's own word for them | new |
| The Lamp | The artificial sun lit at the hub to keep the ring lit through each eclipse | `docs/eclipse-false-sun-spec.md` |
| The Veil | The cloak that kept the ring's own light from being seen from outside. It has failed. | new; proposed home: the outer blades of the projector towers |
| The Count | The grid pulse read as a census of everything on the floor | `docs/emitter-grid-climate-machines-spec.md` part G |
| Ward | The district around one spire: about 314 km along the ring by 53 km across. 595 wards. | emitter site registry |
| Attention | How alert the Lamplighter is in one ward, minute to minute | new |
| Standing | What class it files your expedition under, ring-wide, over days | new |
| The Fever | The megastorm, once it is used as a purge | `docs/megastorm-design.md` |
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
| The megastorm is a wall of cloud that sweeps the whole ring at 350 m/s, triggered "somewhere along the game story", then ever-present. | megastorm design | The Fever (section 2.5). |
| "When the floor hums, the wall is coming." The floor carries the storm's pounding ahead of it. | megastorm 5.3 | The floor carries sound both ways. The player hears the storm through it, and the ring hears the player through it. |
| The containment field holds the air in, passes bodies with a ripple, and has a policy value of pass, resist or solid. | field design, decisions 2026-11-04 | The field is a sense (it feels crossings) and a door (the Closing). |
| The suit assistant is a Subnautica-style voice and "not a ring AI". | eclipse spec decisions, PDA handoff | Kept exactly. Section 6. |
| The forests are maple, fir and pine. | README | The garden is Earth's. Section 1.6. |
| The scale-anchor towers had no accepted identity. | vastness plan, user answer 2 | Removed (user, 2026-11-21). Section 2.2 re-homes what they would have shown; the removal order is in 7.5. |

### 1.2 The core truth

Adopted by the user on 2026-11-21 with two changes: the ring is at another star, and the false sun is a working light, not a beacon.

Long before there were cities on Earth, travellers crossed our system. They did not land. They took cuttings of what lived there, as they did at every living world on their road, and carried them on. At the next star along, around a giant planet in that star's temperate zone, they built a house: a ring spun for the gravity of the world the cuttings came from, roofed with a field and planted with that world's life. They hid it, as they hid all their houses, under a veil that let none of its own light escape. Their law was that a young world should not find its elders by accident. Then they moved on and left a caretaker to keep the house for those of their people who would follow.

No one followed. The caretaker kept the house anyway. It ran the weather, mended what broke and counted what lived. Every eight hours the giant's shadow takes the whole ring into total darkness for a full ring day, and the builders did not want their garden left in the dark. So at every eclipse the caretaker lights a small sun at the hub and holds it there until the star comes back. Under the veil, nothing outside could ever see it.

It has done this for tens of thousands of years. It is running out of parts. It has let six of its two hundred weather engines die so that the rest can live. The Lamp and the Veil drink from the same spires, and as spires failed it kept the Lamp whole and let the Veil's share thin, trusting a margin that ran out. The instrument that would have told it so faces outward, and it was written off long ago. The caretaker does not know that the house can be seen.

In the near future, an instrument built to study the planets of nearby stars records something beside a giant world: a light that comes on inside the planet's shadow for seventy-five minutes every eight hours, the colour of a sun, with none of its star's fingerprints. A ship is sent. Its crew sleep through a crossing longer than a working life. An expedition lands. The caretaker registers a small warm body moving through a forest, classes it with the deer, and goes on with its work, because nothing from outside can find the house.

Then the animal starts using tools.

The emotional centre is a reversal the player earns slowly. "You are an intruder" turns out to be true in a sadder way than it first sounds. The light humanity followed was never a signal. It was a night-light for a garden, in a house built to be unseen, kept by something that has been alone so long that it does not know what to do with a visitor, and does not yet know it has one.

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
- The spec's rule that dead emitters make the sun dimmer is the same shortage that starved the Veil.

**The Veil's failure as a story beat.** Why it failed: the caretaker's own triage, as above. Whether it knows: no, until the end of Act 1 (section 5.2). The ship's radar sweep is the first proof that something outside can see the house, and the Closing is its answer. That turns the first act's ending into the moment the ring learns it has been found. Whether the Veil can be mended is new question 4 in section 9.

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
- **Its flaw.** It is sentient enough to hope and therefore sentient enough to avoid a conclusion. See 1.5. The same habit keeps it from looking outward: it does not know the Veil has failed (section 1.2).
- **A key line for the abide path:** "You are counted." It is the moment the ring stops treating you as weather.

### 1.5 What it protects: three layers

The player's answer to "what is the AI protecting?" changes twice. Each layer is true, and each one reframes the last.

| Layer | What the player believes | Where it is learned | What it explains |
|---|---|---|---|
| 1. The machines | It guards its infrastructure, like any security system | Act 1, at the first spire | Doors, lockouts, drones |
| 2. The garden and the Count | It keeps a living collection in exact balance for its owners. Everything is counted; you are not. | Act 2, at an offline Wall Engine and from the house rules | Why taking, digging and burning matter more than trespass. Why dead machines are free to strip. Why the Fever hurts it. |
| 3. The vigil | It protects its belief that the Expected are coming. The proof that they are not is in its own archive, sealed away from itself. | Act 3, under the main spire and at the Lamp | Why the best-defended thing on the ring is a record, not a weapon. Why it edits the suit assistant's memory. Why it has never invoked the inheritance clause. |

Layer 3 is what makes it a character and not a mechanism. The most dangerous thing the player can carry is not a bomb but evidence.

### 1.6 Why the garden is Earth's

The cuttings were taken from Earth, the last living world on the builders' road before this star, and carried here. That is the whole reason, and it is delivered in two beats:

1. **Early, as an unexplained fact.** In the first hour the suit assistant analyses a leaf: "This is a maple. It matches the terrestrial sugar maple closely. Separation on the order of thirty thousand years, low confidence. I can't explain that." The assistant's honesty rule (section 6) makes this land: it reports and does not speculate.
2. **Late, as the answer,** at the wall top and in the archive (section 1.8, discoveries 5 and 6).

One consequence worth keeping in reserve: the cuttings are older than our history. If the game ever ships animals, the roster should be late-Pleistocene, creatures Earth no longer has. The ring would then hold what the Earth lost without the plot ever being about restoring it. This is an art cost to weigh later, not a commitment.

### 1.7 Humanity's side

- **The discovery.** An instrument built to study the planets of nearby stars finds a light beside a giant world: on for 75 minutes, every 7.83 hours, always inside the planet's shadow, the colour of a sun, without its star's signature. No natural thing does that.
- **The mission.** A crewed ship, launched in the near future. The launch is near-future; the arrival is not. The crossing takes longer than a working life and the crew sleep through it. By the time the player lands, the people who sent them are old or dead, and a question sent home takes more than a decade to reach it.
- **A mirror worth using.** The expedition is an errand that has outlived its senders. So is the Lamplighter's. Nobody says so aloud until Act 3.
- **Who the player is.** Solo play is one surveyor on the floor; co-op is two to four.
- **The ship.** It stays in orbit and is never a place the player walks around in. It is a lifeline (supply drops, analysis, the long view of the whole ring from above) and a voice. It is also the only human authority within light-years, which is why its orders carry such weight and why refusing them matters.
- **The ship has an agenda.** Its charter has a second objective, revealed in Act 2: to judge the ring for settlement. After that the ship pushes the player towards exactly the acts the ring punishes: take a core, plant survey charges, force the archive. What the mission is carrying to settle with is new question 3 in section 9.
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
| 4 | **It has rules.** | First spire and after | Boundary signals, and the assistant's growing rulebook of observed cause and effect. | Section 2.6 |
| 5 | **It is failing.** It has written parts of itself off. | An offline Wall Engine, far from the landing | A storm that has stood in one place for centuries. Menders that walk past dead machinery. The first choice that matters: repair it or strip it. | Wall Engines, parked storm or drought |
| 6 | **It was hidden, and it is waiting.** The house was built to be unseen and its light was never a signal. It was built for someone else, and they are late. | The wall top, on the vacuum side of the field | The view outward. The outer blades of the towers, cold. The watch post that faces the road. The schedule of arrivals. | Wall-top walk, projector towers (built) |
| 7 | **No one is coming.** | The archive under the main spire | The record of what the hub has heard since the road went quiet: nothing. The inheritance clause. | Main reactor (asset built) |
| 8 | **It knows.** It sealed that record away from itself. What it protects is the waiting. | The main spire crown, during an eclipse | The Lamplighter speaks, in borrowed words, as the Lamp lights around you. | Main spire, eclipse ignition |

Order is fixed for 1 to 4, which are the onboarding. Between 4 and 5 comes the Closing (section 5.2), when the Lamplighter learns it has been seen; the player sees only that the sky has shut, and discovery 6 explains why. Discoveries 5 and 6 can come in either order. Discovery 7 requires 5 and 6, and 8 requires 7.

### 1.9 Endings

Four, chosen by what the player does at the crown with what they know. None is the "good" one.

- **Counted.** Reachable with high Standing. The player shows the Lamplighter the record, and it accepts the clause: the house passes to the life of the world it was stocked from, on the house's terms and in the house's numbers. The field opens. It leaves the Veil down, and for the first time the Lamp is a light kept on for someone. The player returns to the ship carrying an invitation with conditions its charter was never written to accept.
- **The quiet door.** The player leaves the archive sealed and the vigil intact. The Lamplighter opens the sky once. If the Veil has been mended, the house goes dark to the outside behind them, and in a dozen years Earth's instruments will lose the light. What the player tells the ship afterwards, the truth or "it's dead, there's nothing here", is the last choice.
- **The keys.** The virus ending. The player takes the main spire and cuts the Lamplighter out of its own house. The Fever stops. So does everything else, slowly. The ship gets its prize and the player watches the wall lights begin to go out.
- **The keeping.** The player stays, as the first resident the house has ever had, and takes up part of the work. This is also the frame for play after the story: the endless game is maintenance. Bring back the six dead engines. Relight the dead emitters. Bring the Lamp to full brightness.

### 1.10 Alternatives considered for the core truth

- **Built for us (an invitation whose key never arrived).** Warm, and it makes "intruder" ironic. Rejected as the main truth because it removes the Lamplighter's dilemma: it would simply be waiting for humans. A trace survives as the inheritance clause.
- **A quarantine (the rule is that nothing leaves).** It gives "get home" real bite, and the Closing borrows from it. Rejected as the main truth because it needs a contaminant, and every contaminant is a monster or a plague in the end.
- **A backup of Earth with a reset tool.** The strongest stakes, but it is the plot of a well-known game about a terraforming AI, and it turns the megastorm into a doomsday device instead of a fever.

### 1.11 Tone rules

- The ring is never evil and never cute. It is busy.
- Nothing on the ring was built to kill. Things that kill the player are maintenance tools, weather and physics pointed at them.
- The player is always small. Power fantasy comes from understanding, not from firepower. A firearm is a few answers, counted in rounds (section 3.12).
- Realism by default, as elsewhere in the project. The fiction asks for four big allowances (the field, the Lamp, the Veil and the Count) and the rest should follow from physics the project already simulates.

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
- **On the suit.** The assistant keeps a ward activity estimate, built only from what it has observed: a watcher overhead, a door that changed state, a Count that caught a signature, weather that followed an act. It is shown on the HUD with its confidence. This is the main readout, and it obeys the honesty rule of section 6: it is an estimate of behaviour, never a reading of the ring's mind.
- **On the spires and sites.** A site's own lights, in the project's existing teal, shift towards amber and then white as its ward climbs the ladder. This reads within a few kilometres of a spire.
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

**The same five-minute beat serves both styles with opposite actions.** The player who is hiding goes dark for the Count. The player who is abiding stands in the open for it, quiet and visible, and lets themselves be counted. A run of clean counts builds Regard slowly. It is habituation: the ring gets used to you.

### 2.5 The ladder

| Class | Standing | How the ring sees you | What it does | What moves you out |
|---|---|---|---|---|
| **Uncounted** | unset | Nothing. A field ripple it logged as debris. | Nothing | The first Count that catches a signature, or the first threshold |
| **Fauna** | unset | A warm body. Wildlife. | Nothing. Doors do not open, because they do not open for deer. | Tool use seen repeatedly, or one loud act |
| **Anomaly** | around 0 | An animal that uses energy. Worth a look. | It looks. A watcher arrives and follows. The Count lingers. It begins to mark boundaries and observe what you do about them. | Your answer to its signals |
| **Tolerated** | +25 and up | A harmless resident | Boundary signals become guidance. Some doors stand open. Sockets you have used stay live. Watchers stop following. | More Regard, or harm |
| **Guest** | +60 and up | Someone the house can serve | Site doors open. The Deep Line carries you. Its buildings shelter you from the Fever. It begins to communicate. | Story (Counted, in Act 3), or harm |
| **Trespasser** | -25 and down | A pest in the works | Lockouts, power cut to anything you used, transit refused, wardens that block and drain, storms steered over you, the uplink jammed | Regard, or absence |
| **Infection** | -60 and down | Damage that spreads | Lethal response: purgers, called lightning, sealed and vented rooms, no supply drops in your ward, your caches found and destroyed, the suit assistant attacked | Sustained absence over several ring days, or a major act of restitution |
| **Purge** | -90, or the Act 2 story beat | A threat to the house | The Fever: the megastorm is triggered and laps the ring. Everything else it does is subordinated to it. | The story (section 5) |

**On the megastorm.** The user's vision is a threat that is ever-present once the story triggers it. This plan keeps that: the Fever begins at the Act 2 story beat, or earlier if a player drives Standing to Purge first, and it does not stop until an ending. What Standing changes afterwards is how the Fever treats the player:
- A Guest finds the ring's own buildings open as the wall approaches. The megastorm design already states that authored structures are never flattened.
- An Infection finds them sealed and must dig, build to a rating or outrun it.
- The inside of the wall is the one place on the ring where the Count sees nothing. A player who can survive in there for the 71 seconds of the wall and the long rain behind it can do anything.

**Why it is a fever and not a weapon.** A 60 kPa wall flattens forest as well as intruders. Each lap costs the garden, strains the engines and dims the Lamp. The Lamplighter resorts to it the way a body resorts to a temperature it cannot sustain. This gives the virus path its moral weight, and it gives the Lamplighter a standing reason to want the infection over.

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

**How the player learns them.** No tutorial text states a rule.
1. **The ring marks its boundaries** with a small, consistent vocabulary of signals in the media it has: light on its structures, a tone through the floor, a door that closes as you approach. Roughly: "seen", "stop" and "allowed".
2. **The suit assistant keeps a rulebook.** It records what preceded what, with a count and a confidence: "Door sealed four seconds after the cutter powered up. Observed three times of three." It never states intent (section 6). The player turns observations into rules by experiment, and in co-op the rulebook is shared.
3. **The ship adds the long view** from orbit: "Every time you dig, the gap in the wall lights flickers."
4. **Every warning is a test.** Obeying a boundary signal is the largest single source of Regard, because it tells the ring that the animal responds to signals. This is also how a hostile player stands down.

### 2.7 How it uses each pushback channel

| Channel | Anomaly | Trespasser | Infection | Purge |
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
| **Warden** | Its hands | Blocks, herds, and discharges a pulse that drains suit power. Non-lethal until Infection. | Makes Trespasser dangerous without making it a firefight. |
| **Mender** | Its healing | Walks set routes inside sites, repairing. Ignores you unless you interfere. Ignores dead machinery, which is how the player learns rule 3. | The construct an abiding player works alongside and a virus player harvests. |
| **Purger** | Its antibodies | Fast and rare. It does not carry a gun: it designates a target and the ring discharges on the mark. | Reuses the lightning systems already built. The counterplay is physical: break the line, get low, kill the designator. |

Each ward holds a small stock (for example two watchers and a warden). Destroyed constructs are replaced slowly from the reactor, or from neighbouring wards by the Deep Line. A ward can be cleared for a while. It cannot be cleared for free. How each construct stands up to gunfire is in section 3.12.

### 2.9 Adaptation

The user asked for an AI that adapts. The cheap, legible form is a tactic ledger.
- The director tags what the player relies on: storm cover, cave routes, river travel, night moves, door cutting, power taps, killing watchers, hiding at the Count.
- When one tactic has beaten it a set number of times while the player is an Anomaly or worse, it fields a specific counter. If you always hide in storms, it clears the sky over you. If you always cut the same kind of door, the door changes. If you kill watchers, they stop coming alone.
- Each counter is announced by a visible change, and the assistant notes it: "That is the third time. The door cycle is different."
- Counters persist. The ring forgives harm but keeps what it has learned.
- **It learns what a gun is.** A firearm is a tool to the ring until it has lost constructs to one. After that, a weapon in the hand near a construct or inside a site raises Attention by itself (section 3.12).

It is a table of about a dozen rows, not machine learning, and every row is a piece of authored content.

### 2.10 De-escalation

- **Attention** fades by itself, faster the further the player goes.
- **Obeying a signal** is immediate Regard at any level.
- **Restitution** works at every level: return it, mend it, replant it.
- **Absence** heals negative Standing slowly, measured in ring days without a new offence.
- **From Infection** the road back is long but always open: several ring days out of sight, or one large act of repair.
- **Death** clears the ward's Attention. To the ring, the infection there is over (section 3.11).
- **The Fever** does not de-escalate. It ends with the story.

### 2.11 Co-op

- **Signature adds up.** Four suits, four radios and four sets of jump jets are louder than one. A careless team is noticed sooner.
- **Attention is local, so a team can split it.** One player draws a ward's attention while another works in the next. This is the heist structure, and it is the best argument for co-op in this design.
- **Standing is shared.** One expedition has one reputation. A player who goes loud spends it for everyone. Among friends this is a feature, and it is how the two play styles collide inside one team.
- **Isolation is a co-op weapon.** At Trespasser the ring degrades the team's shared markers and voice link near live spires. At Infection it separates players with doors and weather. Each suit assistant belongs to one player, so the ring can make two assistants disagree.
- **Responses scale gently:** more watchers per ward with more players, never more than the ward's stock.
- **Solo and co-op use the same rules.** A solo player gets the ship's analyst as the second pair of eyes that a team gets from each other.

## 3. Core loops at three timescales

### 3.1 Four clocks

The pacing rests on four clocks. All four already run in the engine, are deterministic and are saved with the world.

| Clock | Period | What it does to play | State |
|---|---|---|---|
| **The pulse** | 5 minutes | The rhythm of the moment: when to be loud, when to be still | Built (visual and clock) |
| **The day** | 75 minutes, a little over half an hour of light | The rhythm of an expedition: light, temperature, solar charging, and nights when the pulse can be watched | Built |
| **The eclipse** | 75 minutes of totality every 7.83 hours; the first comes about 3.3 hours into a new world | The session event. The ring does not count. It is pitch dark before ignition and after the collapse. The spires are sacred. | Built |
| **The megastorm** | A lap in 24.9 hours of play at 350 m/s. The floor hums from 105 minutes out; the wall lasts 71 seconds and the rain behind it 16 minutes. | The rhythm of the late campaign: shelter, survive, rebuild | Built without damage |

If play tests want eclipses more often, the celestial time scale is an existing setting. That is tuning, not design.

### 3.2 The moment-to-moment loop (seconds to minutes)

**Move, read, act, be still.**

- **Move.** Walking is 2 m/s and sprinting 7 m/s. At that speed the terrain is the content: slope, footing, river class, canopy, the lee of a ridge.
- **Read.** The sky for weather, the arch for the pulse and the megastorm, the spire lights and the suit's ward estimate for the ring's mood, the suit for power and warmth, the assistant's pulse timer.
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
3. A spine discovery changes the whole ring: the sky closes, the ship's purpose comes out, the Fever begins.
4. The changed ring makes the home wards feel different and sends the player further.
5. After the Fever begins, each lap adds a slow outer loop: know where the wall is, be under something rated when it arrives, mend what it broke.

### 3.5 Survival: the blend (decided 2026-11-21)

**Suit first, body second.** The suit's meters move fast and create tension in the moment. The body's meters move slowly and shape expeditions. While the suit has power it buffers the body: it holds warmth and stretches water. When the suit is shut down or damaged, the player falls back to being an animal: fire, shelter, dry clothes, a stream.

This has a consequence worth designing around. Being an animal is exactly what the ring tolerates. So the abide path naturally becomes the full-survival game, and the virus path naturally becomes the suit-and-power game. The user asked for a mix of both kinds of survival; this gives each kind a home.

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
- **Camps, not a fortress.** After the Fever begins, anything unrated is flattened each lap, and the megastorm design already gives ratings for materials. The right shape for player building is therefore several small, dug-in, rated shelters and caches, with maintenance as the cost. The user's megastorm notes ask for exactly this: infrastructure that fails "if not well maintained".
- **The pulse governor.** A mid-game foothold upgrade that shuts the camp down for each pulse automatically. A good example of progression that removes a chore the player has already mastered.
- **Shelter makes a triangle.** Dig (rule 2 and the floor sense), build to a rating (materials and upkeep), or use the ring's own buildings (Standing, or a break-in). Each play style has an answer and none is free.
- **The ship is a voice and a lifeline, not a level.** No ship interior is needed. After the Closing it can be reached only with real power: from the lander's antenna, from high ground or from a dark ward. Talking to home is loud.

### 3.9 Progression

Three tracks. The third has two forms, one for each play style.

| Track | What grows | Examples |
|---|---|---|
| **Gear** | What the player can survive and reach | Bigger cells, a rebreather for altitude, a rated shelter module, hybrid tools |
| **Knowledge** | What the player understands | The pulse timetable, the rulebook, the signal vocabulary, the map of wards and blind spots, the Deep Line map, the database |
| **Standing** (abide) | What the ring will do for the player | Open doors, live sockets, transit, shelter in the Fever, speech |
| **Leverage** (virus) | What the player can make the ring do | Stolen cores that force doors, overrides that hold a lift, dark wards held as territory |

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
1. **The ring uses no lethal force on a player above Infection.** Watchers never attack. Wardens block and drain. A player whose Standing is above -60 is never shot at, struck or hunted.
2. **Carrying a weapon is never an offence.**
3. **Ammunition is too scarce at the start to make fighting the easy answer for anyone.** The first hours play the same for both styles, and the virus path opens as its supply does.
4. **Every job on the spine can be done without firing.** Each threshold keeps its three keys (section 3.9).

An abiding player's weapon is for the unexpected. Finishing the story with the starting magazines untouched should be possible, and worth marking.

**Hunting** with a firearm falls under rule 1 like any other taking. Within a ward's tolerance it is an animal feeding itself.

**Still out: human enemies.** Nothing in this plan puts a person in the sights. Firearms make that worth confirming; it is new question 2 in section 9.

## 4. The two play styles, side by side

| | **Abide** | **Virus** |
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
| **The Fever** | The ring's buildings open as the wall comes | Dig, build to a rating, outrun it, or work inside it |
| **Progress is measured in** | Standing | Leverage and dark wards |
| **The world over time** | Dead engines relit. Parked storms clear. The Lamp brightens. | Wards go dark. The Lamp dims. Menders work on your damage. |
| **The suit assistant** | Left alone, and later used as an interpreter | Jammed, doubted and intruded on |
| **Risk** | Slow, and exposed to the ship's impatience | Fast, and exposed to everything else |
| **Weapons** | A sidearm that may never be fired | Carbine, coil rifle, charges, hybrids |
| **Fighting** | Little to none. The ring uses no lethal force on a player above Infection (section 3.12). | As much as the player starts, limited by ammunition and by what each kill costs |
| **Endings in reach** | Counted, the quiet door, the keeping | The keys, the quiet door |

**Abiding is a discipline, not a menu option.** It is active play: working out the rules by experiment, doing repair jobs that are expeditions in themselves, and holding to the rules while hunger, cold and the ship all argue for breaking them. The danger in designing this path is that it becomes "do nothing". The cure is that the ring only gives things to players who do something for it.

**The virus path is a strategy game inside the survival game.** Killing a spire's feed takes its ward dark: no Count, no responders. A dark ward is territory. The ring retakes it with menders and wardens arriving by the Deep Line, unless the player holds it. The player literally spreads, and the purge follows the spread.

**The shared middle is sneaking.** Both styles spend most of their time unseen. A third way to play falls out without extra work: the ghost, who finishes the game still classed as Fauna and never earns or loses anything with the ring.

**Switching.**
- Standing is one number, so a player is never locked into a side. Harm and Regard both always count.
- Coming back from the virus side takes absence and repair. It is always possible short of the endings.
- Going over from the abide side is instant. Trust is slow to build and quick to spend.
- **What persists after a switch:** broken machines stay broken until someone mends them, the Lamp keeps its brightness, and the ring keeps every counter it has learned (section 2.9). The ring forgives; it does not forget.
- **One act cannot be walked back quickly:** breaking rule 6. Interfering with the Lamp drops Standing straight to Infection.

## 5. Story spine and structure

### 5.1 Recommendation

**An open world with a story spine**, as the user leans. Specifically:
- The world is open from the landing. Nothing is walled off. Places are closed by distance, weather, altitude, air and doors, and every door has three keys (section 3.9).
- The spine is the eight discoveries of section 1.8, grouped into three acts.
- **Spine sites are roles, not coordinates.** "The nearest spire", "the nearest offline Wall Engine", "the wall top above it", "the main spire". Every seed already has all of them in its registries, so the story works on any world and never needs hand placement. Only the landing candidates need a finder that guarantees a fair start.
- Three spine beats change the state of the whole ring: the Closing, the ship's second objective and the Fever. These are what make it a story and not a checklist.
- Expeditions are the unit of a session inside the open world.

### 5.2 The three acts

**Act 1: Fauna.** Landing, the sealed start, the first night, the first eclipse seen from the ground. The first spire and the first time the ring looks back. The rulebook begins. Discoveries 1 to 4.
- *It ends with the Closing.* The ship, impatient, sweeps the hub with radar. To the Lamplighter this is the first proof that something outside can see the house: the Veil has failed and it never knew. It answers by turning the field solid to craft. No more drops, no ascent, and the uplink needs real power. The player sees only that the sky has shut; the reason is discovery 6. This beat is caused by the ship, so it is fair to every play style, and it uses a policy value the field already has.

**Act 2: The long way round.** Home is now on the far side of a door only the ring can open, so the player must go and find out what the ring is. That means the first reactor descent, the first ride on the Deep Line, and a journey of thousands of kilometres to a dead Wall Engine and the wall top above it. Discoveries 5 and 6. The repair-or-strip choice at the engine is the first one the whole ring can see: a storm that has stood for centuries either clears over the following hours, or the player leaves with the best salvage in the game.
- The distances are forced by an existing decision: offline engines are at least ten stations apart and never near the main spire. So the spine is ring-scale by construction, and the Deep Line is on it.
- *It ends with the Fever.* The ship reveals the second objective in its charter, to judge the ring for settlement, and acts on it: an automated survey lander is driven through the closed field by force and starts drilling. The megastorm forms. If the player has already driven Standing to Purge, the Fever is theirs and the ship's lander merely confirms it.

**Act 3: The Lamp.** The megastorm is now always somewhere on the arch. The player makes for the main spire, by whichever of the three keys they hold. Down to the archive in the main reactor: discovery 7. Up four kilometres to the crown, timed so the player is there as an eclipse begins: discovery 8 and the endings, with 595 beams converging overhead.

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

Suggested replacement wording for the rule, for the user to approve: "The assistant is not the ring's intelligence and has no special knowledge of it. It may report what the resident system has been observed to do, and may offer a hypothesis if it labels it as one with its confidence. It never states the resident system's intent, motive or history as fact."

### 6.3 What it does in the loops

- **Timers.** It gives a pulse countdown only after it has observed two passes itself. The megastorm and eclipse line tables already exist.
- **The rulebook** (section 2.6): observations, counts, confidence. This is its most important screen.
- **Analysis.** The leaf. The air. Salvage.
- **Vitals and warnings**, with critical lines always authored and never rewritten, as the megastorm design already requires.
- **The database**, unlocked by first observation, as "Structural resonance" already is.

### 6.4 Interference, and the rule that keeps it fair

The user wants the ring to interfere with the suit AI. Done carelessly, that teaches the player to ignore the assistant. Three tiers, each with a tell:

| Tier | From | What happens | How the player knows |
|---|---|---|---|
| **Denial** | Trespasser | Functions are lost near live spires: the uplink, team markers, scan range, the map | The assistant says so at once: "I've lost the ship." |
| **Doubt** | Infection | Sensors are fed noise. Readings widen. | The assistant reports its own confidence, as it always has. Now the numbers are bad. |
| **Intrusion** | Infection, and the Act 3 story | A waypoint that it did not set. A line spoken in its voice that it did not generate. A gap in its records. | See the rule below |

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
| 252 projector towers and the wall-top walk | Built | Discovery 6; the virus endgame's targets | Site kit |
| Megastorm model, hazard queries (overpressure, rain, wake, cover), the structure-rating interface, its PDA lines | Built, with no damage | The Fever | Damage and ratings |
| Storms, lightning, storm arrival, the wet world, shelter, rain audio | Built | Inputs to body temperature; cover from the Count; reading the sky | The exposure model |
| Rivers with danger classes, lakes, ocean, swimming, underwater | Built; rivers frozen as generation 2 | Water, routes, cover, the air meter | Flow acting on the player (T9i) |
| Forest and grass | Built, in revamp | Canopy cover, forage, fuel | Forage nodes |
| Surface terrain edits (2 m lattice, persisted) | Built | Pits, berms and trenches: real cover from the megastorm's overpressure and ground lightning. Camp platforms. | Volume digging is not in the default backend (7.5) |
| Day and night, the opposite arch, the far view | Built | Reading the ring from far off: where the megastorm is, which engines are dark | None |
| Minimap and trackable markers | Built | The assistant's map; caches; teammates | Screens |
| PDA announcer, subtitles, line tables, database unlocks | Built with subtitles; the voice pipeline is prototyped | All narrative delivery | The rulebook; voice |
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
| HUD and the assistant's screens | L | Vitals, map, rulebook, database, inventory |
| Signature and senses | M | Pure model. Reads the pulse clock, the field bus, cover queries. |
| The director: Attention, Standing, response scheduling, the tactic ledger | L | Pure C#, headless-testable, ticked on the weather clock. The design risk is tuning, not code. |
| Site kit: doors, sockets, terminals, lifts, power state | L, then M per kind of site | The spire facility first. Interiors are the real content cost. |
| Directed weather | M | Storms the director can place, steer and save. Must stay a pure function of seed, time and saved events (7.5). |
| Megastorm damage and shelter ratings | M | The queries and interfaces exist |
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
8. **Tower removal (a small work order).** The user decided on 2026-11-21 to remove the scale-anchor towers. The order: take the `ScaleAnchors` node and `RingScaleAnchorLayer` out of the gameplay scene, with their shader, tests and manifest entries; update the README line about landmark towers; and close vastness work order V2-4 (tower livery) and the contingent V7b (chain lines of masts), which no longer have a subject. The primary agent should decide whether the removal follows the new-worlds-only rule. The vastness plan describes the towers as drawn by the main camera only, with no shadows and no identity, which argues for removing them everywhere.
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
  - The Count: the timer, going dark, standing to be counted, cover from canopy, rock and water.
  - Attention, and the ladder from Uncounted to Trespasser and to Tolerated.
  - One response on each channel: a watcher, doors and lights at the spire facility, a rain cell that may or may not be coincidence.
  - The rulebook with three learnable rules: the Count, closed doors, powered tools near structures.
  - About forty assistant lines, subtitled.
  - One job: fetch a written-off part from the spire's gallery to repair the lander's antenna and raise the ship.
- **Out.** Co-op, weapons and combat, crafting, the underground, the megastorm, the Deep Line, the story.
- **Playable test.** About an hour, unprompted. It passes if the player:
  1. changes what they are doing because the pulse is coming, without being told twice;
  2. can state two of the three rules afterwards in their own words;
  3. is noticed at least once and brings the ward back down at least once;
  4. describes the ring as indifferent at first and attentive later.
- **Unblocks.** The decision to build the rest. If the Count is not fun here, nothing built on it will be.
- **Size.** XL across several orders, roughly 30 to 40. Depends on G0.

### G2. Expedition

- **Goal.** The session loop holds up over several sessions.
- **In.** All the meters and the sealed start. The fabricator and about fifteen recipes. The first dead-ring salvage site. Camps and caches. The glide wing. Forage with ward tolerance (rules 1 and 3). The choice of landing site. Rafts, if T9i has landed.
- **Playable test.** Over three sessions, establish a second camp at least 30 km from the lander and bring back one part that cannot be fabricated.
- **Unblocks.** Content at scale; a game worth playing with someone else.
- **Size.** XL. Depends on G1; benefits from T5, T6 and T9i.

### G3. Company (co-op)

- **Goal.** Two to four players through everything in G1 and G2.
- **Playable test.** Two players split a ward: one draws the watcher while the other goes in. Both come home. The host's save holds it.
- **Unblocks.** Constructs and combat built for a network from the start.
- **Size.** XL. Depends on N0 and G2.

### G4. Immune response

- **Goal.** The whole ladder, in both directions.
- **In.** Wardens, menders and purgers. Firearms, tools and combat (section 3.12). Directed weather. The tactic ledger. Assistant interference, tiers one and two. Megastorm damage and shelter ratings, with the megastorm started from the debug menu. Dark wards.
- **Playable test.** One save played twice, once abiding and once as a virus. Both get inside the spire. The virus run takes a ward dark and holds it for a ring day. The abiding run reaches Guest.
- **Unblocks.** The story, which needs every rung.
- **Size.** XL. Depends on G3.

### G5. The Closing and the long way round (Acts 1 and 2)

- **Goal.** The spine from landing to the Fever.
- **In.** The reactor underground and the Deep Line. Wall Engine presentation and anomaly weather. Repair or strip. The wall top. The ship's voice. The Closing. The Fever's trigger.
- **Playable test.** Play from landing to the megastorm forming on the arch.
- **Unblocks.** Act 3.
- **Size.** XL. Depends on G4, on the paused reactor milestone and on the Wall Engine work in the grid spec.

### G6. The Lamp (Act 3 and the endings)

- **Goal.** The end of the story.
- **In.** Main spire and main reactor gameplay. The archive. The crown during an eclipse. The Lamplighter's speech. Interference tier three. The four endings.
- **Playable test.** Each ending reached from one late save.
- **Size.** XL. Depends on G5.

### G7. The keeping

- **Goal.** The game after the story, and the larger toys.
- **In, each on its own go.** The maintenance loop. Rover and aircraft. Animals. Block building, if the backend allows. More constructs and counters.

**The order, and why.** Seams, then the slice, because the slice is the cheapest way to find out whether the Count is fun. Then the expedition loop, because co-op needs something to do. Then co-op before enemies, for the reason in 7.4. Then the ladder, then the story, because the story needs the ladder.

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

### 9.2 New questions

Four, each raised by an answer above.

1. **Does the Lamplighter know the Veil has failed, and is the failure its own doing?**
   **Recommendation: it does not know, and it caused it.** It kept the Lamp whole as spires died and let the Veil's share thin; the instrument that would have warned it faces outward and was written off. It learns at the end of Act 1, from the ship's radar, and the Closing is its answer. This gives the cloak a cause, makes the AI's first indifference make sense (nothing from outside can find the house) and turns the first act's ending into the moment the ring discovers it has been found. The alternative, that it dropped the Veil knowingly to save power, makes it colder and removes that beat.

2. **What may be shot?**
   **Recommendation: constructs, and animals if they ever ship (under rule 1). No human enemies, and friendly fire off by default in co-op.** Firearms raise the question the first draft could leave alone. Keeping people out of the sights keeps the ship's pressure a matter of orders and conscience, and keeps the game about the ring.

3. **How large is the mission, now that home is a century away?**
   **Recommendation: a small science crew with a sleeping reserve, and a charter that tells them to judge the ring for settlement.** The awake surface team is the player or players. The reserve gives the optional finite-roster mode its fiction. The settlement clause replaces the first draft's "claim", which made sense only when Earth could send more ships. The larger alternative, a ship full of sleeping colonists, raises the stakes of every ending but turns the ship into a second story.

4. **Where does the Veil live, and may the player mend it?**
   **Recommendation: on the outer blades of the existing projector towers, and yes.** The towers already have two blades: the inner one holds the air in, and the outer one would hold the light in. That needs no new asset, and it puts the evidence exactly where discovery 6 already happens, on the vacuum side of the wall top. Mending it is a late act for an abiding player and leads to the quiet door ending, where the house goes dark behind them. It also needs the user's decision on machine state (decision 3), which is now made.

**Smaller choices that can wait:** how often eclipses come; whether the Lamp's late ignition and violent collapse are read as signs of age (section 1.2); whether the question-and-answer assistant becomes a feature; a Pleistocene animal roster; the ship's casting; and every working name in the table in section 0.

**Next step.** G0: three lead design docs (the signature model, the director, the site kit), the co-op-ready contract, and the tower removal order, before any other implementer work.
