# Modstoke Client

Modstoke Client by OpenACAI puts a field tablet and wristwatch in Sons of the
Forest, with navigation, notes, messages and community services. Managed server
admission is optional and disabled by default. Modstoke is not affiliated with
or endorsed by Endnight Games.

Place `Modstoke.Client.dll` and `openacai.mod.json` together in a native package
folder under `Modstoke/Mods`. Client 0.2.8 requires the matching Modstoke SDK
build with equipment, crafting-mat, terrain-surface and local-vitals observations. Keep the Loader
Core and SonsSdk runtime files matched on the host and participating clients. Use Modstoke Admin
0.5.212 rebuilt against the same SDK for the current tablet server features. Servers requiring an
exact Client version must update their requirements before players upgrade;
installing the package does not change admission policy.

The Client uses the game's Unity, TextMeshPro and FMOD assemblies. Do not bundle
extra copies of them, the loader DLL or either original loader.

Use Client 0.2.8 on the host and every participating player. Other players can see
the tablet being held and used, the worn watch and its inspection gestures, and
deployed drones. Nearby players can see the tablet's actual screen, including
messages, notes, games and drone video. Screen sharing uses a 512Ã320 image at
up to two updates per second within 12 metres; busy connections may update less
often. Screens clear when the tablet closes or the feed expires. A privacy filter
is not included. Watch displays remain private. This presentation does not change
the player's equipment or permissions. Use the accompanying Loader/SDK update
so shared services start even when no legacy mods are installed.

Select **Drone controller** in your inventory to take out a separate handheld
controller and open the Drone app. The field tablet keeps its touchscreen design
and can still fly the same drone through **All apps â Drone**. Both devices use
the same flight controls and live video. The controller has its own control deck,
protective rails and antennas; its modeled sticks and switches are visual hardware,
with interaction handled through the screen and keyboard. Nearby updated players
see the selected device and its shared screen. Earlier Client versions cannot
exchange this version's device presentation; update the host and peers together.

Field equipment uses detailed casing meshes and separate polymer, rubber,
composite, metal and solar-coating finishes in inventory and in the world.
The drone uses a narrow coyote airframe, raised dark arms, open propellers,
an underslung battery and a stabilized sensor head. Its compact solar panels
still unfold for charging. The tablet and watch keep their dark olive finish.
Moving rotors, the sensor head and solar panels retain their separate pivots.
Active display pixels are separate from reflective hardware; sleeping inventory
glass still reflects its surroundings. Shared screens use filtered mipmaps to
reduce shimmer at oblique angles. The game's camera effects still apply.

## Your character

The game normally gives you a random person every time you join a world. Open
**Mods → Character** to keep one look: by default the look you get in your next
world is kept, or pick one of the game's eight looks, or go back to a random
look each join. Your choice is saved in `BepInEx/config/Modstoke/character.json`
and applies to every world. It uses the game's own player models, so everyone
sees it, including players without Modstoke. Clothing is still saved with your
character by the game.

With the **Modstoke Characters** package installed beside the Client, set
**Body** to **Modstoke body** and choose **Customise character** to open the
character creator. It shows your character on a turntable: drag to turn it,
use the wheel to zoom, and pick the whole body, face or hands to frame. Choose
a body type, skin tone, eye colour and underwear, and shape the body with the
sliders. Changes show on your character as you make them; **Done** keeps them
and leaving without it puts your saved look back. Clothing you wear hides the
parts of the body it covers.

Players who also have the character package see your custom body when the host
runs Modstoke Client; everyone else sees your game look. Hair is not part of
this version, and the bodies use the game's skeleton, so they stand at the
game's height.

The base package has 2K textures. The optional **Modstoke Characters HD** (4K)
and **Modstoke Characters UHD** (8K body) packs add sharper skin; choose
**Texture detail** on the Character page, or leave it on **Auto** to use the
best installed pack your graphics memory allows.

The separate **Modstoke Characters Adult** package is for players who are 18 or
older where adult content is legal. After installing it, turn on **Adult
content** on the Character page to confirm that. The setting stays on your PC
and is never sent. You can then choose no underwear; other players see that
only if they made the same choice, and everyone else sees underwear.

## Supplies and field conditions

**All apps â Pack** lets you search item definitions, check local stock, inspect
current and pending equipment, find compatible ammunition and plan ingredient
quantities. It does not move items or craft them.
In Crafting, **Makes this** finds recipes for the selected item; **Uses this**
finds recipes that consume or reuse it. **Draft supplies** prepares a Notebook
note with the observed requirements and shortages. Review it and choose Save
note to keep it; existing unsaved notes are preserved.

The **Mat** tab can retain a reading from your open inventory. Enable **Remember
mat**, put the tablet away, open your inventory, then return to Pack. Mat counts
are compared separately from inventory stock. Remembering is off by default
and clears when the player session ends.

Unknown readings stay unknown, and sufficient ingredients do not guarantee that
the game will allow a recipe. The catalog can have limited coverage.

**Clock â Conditions** shows the island's observed season and weather alongside
game time. Pack, Health and Conditions work locally without a compatible server.
Community companion controls still use the server's access rules; **Queue status**
and **Queue receipts** distinguish current observations from recorded work.
**Current order** shows the native order and targets for your open companion queue.

## Spaceflight

**All apps â Spaceflight** deploys the game's cave spacecraft in a large, dry,
level clearing. It uses the installed game's hull and cabin assets. Pause on the
ground outside the rear ramp while your island return is prepared automatically, then walk
through the cabin. Look at the pilot chair or either passenger chair and press
**E** to sit. The host and every occupant should use Client 0.2.8 and matching
Loader/SDK files so cabin collision and controls agree.

The pilot's cursor opens on the physical console after sitting down. Click the
switches and buttons or drag the flight stick. Close the rear ramp and
switch on power before flying. The console controls power, dampeners, the ramp,
translation, attitude and destination travel; crew hands reach for its switches.
Flight is locked until the ramp has closed and everybody inside is seated.
Choose **Launch to space**,
then **Travel to world** to approach the unknown world. Descend into its
marked landing basin, stop, switch off power and open the ramp. Press **X** to
stand inside the cabin, then walk outside. Walk to the three beacons
and use **Survey site** in the tablet. Everyone must reboard before the pilot
selects **Return to island**. Land again to leave normally. Emergency island
return remains available from the cockpit.

Press **Tab** for free look and keyboard flight: **W/S** forward/back, **A/D** sideways, **Space/C** up/down,
**Q/E** yaw, arrow keys pitch/roll, **Shift** boost, **F** dampeners. Mouse movement
looks around the cabin. Press **Tab** again to return to the physical console;
**I** also toggles power. Walking inside is supported while the ship is parked.

The destination follows the wet woodland shown in the [official Forest 3 reveal](https://www.youtube.com/watch?v=QJEHAlHFvEk&t=47s):
dark rock shelves and shallow pools, crooked bare trees, fern-like growth and
ribbed aquatic forms under a pale, clouded sky. The rocks and aquatic plants have
authored meshes and surface maps; cliff profiles and vegetation vary around the
landing clearing. Its layout and survey-site names
are authored for this mod; they do not establish the game's map or planetary lore.
It remains a bounded expedition area in the retained island session.
Travel uses an eight-second countdown followed by relocation between regions;
continuous atmospheric ascent and a visible warp journey are not implemented.
The spacecraft and survey progress last for the current session.
While away, player saves retain the captured island return location. Travel is
refused if that save protection cannot be prepared. Multiplayer, native lighting,
landing, cabin walking, chair and console animation, and round-trip save/load
still require in-game acceptance for this build.

## Shared aircraft

With Client 0.2.8 on the host and every participating player, **All apps â Hangar**
deploys a Kestrel K3 with one pilot seat and two passenger seats. Stand outdoors
facing a large, dry clearing. Placement searches nearby positions 14â22 metres
ahead and checks wheel support, door access and the first 24 metres of taxi
clearance. Inspect the rest of the runway before taking off. Approach within 8 metres and choose
**Board as pilot** or **Board as passenger**. The cabin door opens, the tablet
closes and equipment is stowed before boarding. Wait for the door to close before
taxiing. Everyone sees the same host-controlled plane and seated crew.

The cabin has contoured upholstered seats, fitted lap and shoulder harnesses,
recessed instruments and moving yokes, pedals, switches and throttle. Seated crew
follow those controls, including the pilot reaching for switches and throttle.
The first-person view retains your seated body while hiding your own head.
Aircraft sharing requires the same Client version on all participating peers.

Press **I** to start the engine, **Shift/Ctrl** to increase/decrease throttle,
**W/S** to lower/raise the nose, **A/D** to roll and **Q/E** to steer the rudder.
**Space** brakes, **F** cycles flaps and **L** controls lights. Allow a long, clear
takeoff run and raise the nose gently. The airspeed gauge indicates airflow
adjusted for air density; it is not ground speed. Extra passengers and mountain
altitude increase the runway needed and reduce climb performance.

The wing can stall in a steep turn as well as during slow flight. Lower the nose
to reduce angle of attack, level the wings gently and add power to recover.
Flaps deploy progressively, adding lift and drag. Near the ground, reduced
induced drag can make the plane float during the flare. With the engine stopped,
the plane continues to glide and the windmilling propeller adds drag. Hard impacts
damage the airframe. Density follows a standard atmosphere.

Wind now affects the wings, tail and propeller. Expect crosswind drift, changing
airspeed in gusts and uneven loading across the wings. Wind strengthens above the
surface, and heavier rain or snow increases the simulated wind and turbulence
gradually. The host controls the conditions for everyone; the airspeed gauge and
airflow sound use speed through the air while wheels and exits use ground motion.
This is an estimated weather model, not a reading of the game's vegetation wind
or a calibrated real-aircraft simulation. Update the host and all participating
clients together: earlier versions cannot join this version's aircraft service.

**Tab** switches between looking around and a pointer for the dashboard switches
and accessible cockpit controls. **X** requests an exit after landing and
slowing below 2 m/s. The door opens before you leave, and the host checks the exit
is clear before releasing the seat. Your original equipment is restored afterward.
After a rollover, let the wreck come to rest and press **X**. The host checks
that the hull is supported and looks for clear standing room around the cabin.

The initial Hangar includes free **Service plane** support for refuelling and
repairs while the cabin is empty and the engine is off. The hosting player can
recover the parked test aircraft. Finding and rebuilding an abandoned plane is
not part of this version. One plane belongs to each world; normal game saves
retain its fuel, condition and last parked location. A save made in flight
restores the aircraft at that last parked location, with no occupied seats.

## Survey drone

Open **All apps â Drone**, or select the drone on the inventory mat. Each player
receives one unarmed aircraft and two rechargeable packs for that world. One pack
is installed; the spare appears beside the drone in inventory. In multiplayer,
the host and joining players need the updated Client to exchange the kit and
flight state. A deployed aircraft stays in the world until recovered; reconnecting
does not issue a second kit. Ordinary game
batteries keep their existing uses.

Choose **Deploy** on clear, level ground, **Connect controls**, then **Arm motors**. Hold
**Space** to climb, **Left Ctrl** to descend, **WASD** to move and **Q/E** to turn.
The camera and flight readings appear on the tablet. Releasing the controls
stabilizes the aircraft; **Land**, closing the flight app, losing the control link,
or running low on power requests a landing. The player remains vulnerable while
using the tablet.

Wind pushes and turns the drone through aerodynamic forces on its hull and
four motor assemblies. Stabilization compensates within the motors' limits;
strong gusts can cause drift, and working harder uses more battery. Air drag
also acts when the motors are off. A parked, disarmed drone resists light wind
through its skid contact so solar charging can continue normally.

The host uses the same authored weather model as the plane, with drone-sized
areas and forces. Rain and snow strengthen the simulated wind gradually.
These are estimated flight and ground-friction parameters; they do not read
the game's vegetation wind or reproduce a certified real drone's performance.

Deployment follows the ground's slope and checks support beneath both landing
skids. Snow uses the same physical ground checks as other terrain. Steep slopes,
uneven support and nearby obstructions have separate messages; face a smoother,
clear patch and let the aircraft settle before arming.

Before deployment, the Drone app shows a local ground preview with the airframe
footprint and landing skids. Green means the local checks passed; red means the
spot needs attention. The host checks the actual deployment again. Preflight
checks show battery, condition, controls, ground contact, tilt and solar panels.
Hosts running Client 0.1.64 supply the additional ground checks; older hosts show
them as unknown.

**Save recovery pin** records your drone's last radio contact as a private GPS
destination. The contact card shows how long ago it was received. In GPS, use
**Use on watch** for a bearing to that saved location. The pin stays where you
saved it: it does not follow the aircraft or update existing pins. Contact stops
updating outside radio range, with an empty pack or a destroyed airframe. Unsaved
contacts clear when the drone is picked up or you leave the world.

The highlighted action follows the aircraft's current state. **Land & disconnect**
requests a landing; it does not hold the aircraft in the air. **Return to map**
also releases the controls and requests landing, retaining your destination.
Wait for the motors to stop and approach the aircraft before picking it up.

Choose **Fullscreen camera** for a first-person flight view. **Up/Down** tilts
the camera, the **mouse wheel** adjusts 1â3Ã zoom, and **R** resets the lens.
Hold **Shift** for slower, more precise flight inputs. Heading, camera tilt,
zoom, battery and aircraft condition remain visible. **Escape** returns to the
tablet's flight app with controls still connected; **F4** puts it away and requests a landing. The fullscreen view keeps
normal game controls captured and does not move or protect your character.

**Camera / details** adjusts tilt and zoom with touch controls, with Forward and
Ground survey presets. Its tabs show preflight checks, the last recorded contact
and complete operation reports. Framing guides and a compact fullscreen HUD are
optional; camera tilt is relative to the aircraft.

The **Survey** tab adds a center reticle and **Mark surface**, which saves a
private pin at the camera's observed terrain or static surface, up to 250 metres
away. It leaves the camera open and does not change your destination. Actors,
moving objects and unknown surfaces cannot be marked. A surface return does not
establish a safe landing site. The same tab can latch precision controls and
shows the aircraft's direction toward your scout target or pilot. **Flight**
keeps a session report of received telemetry, including gaps; it does not
reconstruct flight outside radio contact.

**Survey â Terrain profile** shows the host's heightmap slope and downhill
direction beneath the aircraft, with the sample location and age. It requires
a host running Client 0.1.72 and the matching SDK. This is a single heightmap
sample; it does not establish landing clearance or describe caves, holes and
constructed surfaces. Older hosts leave the reading unavailable.

The airframe has a beveled composite casing, motor housings, removable-pack
latches, protected tilting optics and utility rails. The rails and accessory
sockets are visual mounting locations; attachment installation is not implemented.

After landing, approach within four metres to recover the aircraft or swap its
pack. Unfold its solar charger on the ground to charge the installed pack in
daylight with open sky. The hinged leaves take a moment to unfold; charging
starts once they are open. Let them fold fully before arming, swapping packs
or packing the aircraft. Swap packs to charge the spare. The charger is part of
the drone kit; it does not connect to the game's electrical grid. Battery charge,
aircraft condition and deployment position are retained with the world save.
Recovery does not repair crash damage.

Rotors spin up and coast down, with spatial motor sound that follows their load.
Nearby panel and pack operations have short mechanical cues. Tablet and watch
buttons give quiet touch feedback. Device sounds follow the tablet volume,
Silent mode and the game's sound-effects volume. Polymer, rubber and metal
surfaces have fine light-responsive detail without changing the device fit.

The simulated onboard computer handles motor control, stabilization, battery
monitoring and failsafes. Its managed worker uses bounded sensor snapshots;
Unity still applies physics and renders the camera on the game thread. Flight
uses gravity and thrust at four motors. Collisions and supported native impacts
can damage the aircraft. Native enemy-interest registration is included, but
specific curiosity and rock/spear attacks have not been qualified in play.

The host simulates multiplayer aircraft. Custom models and the flight app require
this Client on the host and participating players; unchanged clients do not gain
drone visuals. The initial limits are 16 deployed aircraft and a 180-metre control
radius. Flight feel, camera presentation and multiplayer behavior still need
in-game acceptance for this build.

## Tablet and watch controls

- **F4** opens the tablet directly; selecting it on the mat first is unnecessary.
  From the inventory, F4 closes the mat and waits for the equipment transition.
  The game's GPS action opens its GPS app; its chat key opens Messages when the
  tablet is available.
- Swipe up or choose **Resume** on the wake screen. **Back** retraces navigation,
  **Recent apps** switches apps, and **Home** returns to your favorite apps,
  objective and latest notices. **All apps** opens the searchable app drawer;
  search by name or purpose, filter by category, or choose **Arrange favorites**
  to pin apps to Home, move them earlier or later, and explicitly choose a
  replacement when all eight slots are full. Enter opens a single search result.
  Wake-screen messages, countdown and reminders open their apps directly.
- Drag down from the top edge for notifications and quick settings. Pull down
  again or choose **More controls** for sound, theme and timer controls. **Lock**
  returns to the wake screen; it is a display lock, not player authentication.
- **F4** or **Put away** closes the tablet. **Escape** first closes an open
  drawer, picker, notification reader, settings finder or Recent apps; otherwise
  it puts the tablet away. From a fullscreen app, Escape first returns to the
  tablet. Close chat or
  another menu first. Tablet input does not pause multiplayer.
- Hold **F3** to raise your forearm across your view and read the wristwatch;
  release it to lower your wrist. Settings offers **F9**, **F11** or **None** as
  alternatives. A key already owned by another binding is unavailable. The watch
  temporarily stows both held items and restores them after you lower your wrist,
  unless you select other equipment or open a menu. Put down carried logs or
  planks first. A busy hand action shows a message; ordinary HUD readings remain
  while inspection is unavailable.
- On the watch's clock home screen, **Enter** opens its app selector;
  **arrows** browse and **Enter** selects. **Backspace** goes back, **H** returns
  Home and **Q** opens quick settings. **Escape** lowers the watch. Your right
  index finger presses the screen for each selection; let it finish before
  selecting again. Releasing F3 cancels an unfinished press.
- Watch apps include Health, Navigation, Notices, Countdown, Stopwatch, Mission,
  Reminders and About. On Health, arrows choose a vital. On Countdown, **Left/Right**
  choose a preset before starting; **Enter** starts, pauses or resumes it and
  **Down** prepares a reset for confirmation. Stopwatch uses **Enter** to start
  or pause and **Down** to prepare a reset. While running, **Right** records a
  lap; **Left** opens retained lap history and **Up** returns to the live lap.
  While paused, **Left/Right** browse retained laps. Timing
  continues while the watch is lowered, until the session ends.
- Quick settings offers three themes, a dim night display and private-preview
  hiding. Arrows select a setting and Enter changes it. These preferences are
  saved on this computer and can also be changed in **Settings â Watch & HUD**.
  The stopwatch belongs to the current session; the countdown is shared with
  the tablet.
- On Home, directions open timers, stopwatch, reminders and notices directly.
  **Enter** in Health toggles the overview; in Navigation it toggles compass and
  route views. Idle countdowns accept **Up/Down** minute adjustments. About has
  a six-page controls guide. Reading positions are retained for this session.
- **F6** opens the Loader's Mods menu, including the detailed community command
  editors and managed-admission settings, after putting away your device.

The tablet and watch display the island's time and world day. The day comes
from the game's underlying clock; it is not a PC calendar date or a personal
survival-day counter. Countdown, reminder and stopwatch durations measure
elapsed time, so sleeping or changing the world's time does not skip them.

**Clock â Reminders** schedules up to eight labeled reminders, from 1 to 120
elapsed minutes. Due reminders appear first; use **Snooze due** or confirm
**Dismiss due** to handle the displayed group. They continue
while both devices are put away and appear on the tablet and watch when due.
On the watch, **Left/Right** choose a reminder, **Enter** snoozes a due reminder
for five minutes, and **Down** prepares dismissal. **Enter** confirms;
**Backspace** cancels. **Up** selects the next due or nearest scheduled reminder.
Notices and mission text have numbered pages. Reminders clear when you leave the
world; they are not offline alarms. Silent mode respects your alert settings.

Display settings include a readability sample and brightness presets. Brightness
and volume also have 5% step controls. Display, sound and watch preferences can
be reset separately with confirmation; other sections and saved content are kept.
Reduced motion disables scroll momentum as well as screen transitions.

**Settings â System** shows app activity, foreground time, shared services,
storage status and session uptime. Enable developer information in **About**
to see command queues and dispatch details. Apps pause their foreground activity
when covered, locked or put away; closing an app does not cancel shared timers.
Device services and settings storage run on a dedicated background thread with
bounded command batches. Unity rendering and native game access stay on the
game thread. Modstoke OS is a game simulation, not an Android runtime, and does
not reserve a processor core or run Android applications.

The tablet and raised watch capture native game controls and Loader/SDK
hotkeys through drawing, inspection and stowing. Device keys and controls
remain available. Mods that read raw Unity keys directly are outside SDK
input capture.

The field tablet and watch are starter equipment on the inventory mat. Select
the tablet to close the mat and take it out; select the watch to wear or remove
it. F3 requires the watch to be worn. Device ownership and the watch's worn state
are stored with the player through Modstoke's save extension. These personal
devices cannot be dropped, traded, crafted or assigned to native quick slots.

The tablet retains your existing GPS for native navigation and temporarily
stashes the right-hand item. Putting away the tablet also puts away the GPS
and restores previous equipment where still available. Other players see the
native GPS; custom device IDs are kept out of native equipment and save lists.
The devices include no
redistributed game assets. They are fictional in-game
instruments; they do not run mobile apps or connect to real Bluetooth or
satellite hardware.

## Your field notebook

Home holds a mission note, destination, favorite apps and recent notifications.
**Notebook** creates, edits and deletes local notes; **Health** shows available
local player readings with level gauges. A marker at 25% identifies low readings;
an unavailable value stays unknown. **Watch** shares the wristwatch's countdown and navigation
summary. The shade offers 5, 15 and 30 minute timers with pause, resume and
cancel. Timers continue in standby and end when the game session changes.

On a compatible server, a local profile keeps your last app, favorites, mission,
message drafts, saved notes, GPS pins, routes, destination and recorded trail.
Profiles are separate for each server, world and account, stored under
`BepInEx/config/Modstoke/field-profiles`. Without a persistent world identity,
the notebook works for the current session only. Check the Notebook storage
status before relying on saved work. Received message history, notifications,
server replies and pending confirmations are session data, not saved profiles.

Search notes by title or contents, or sort them by date or title. Switching notes
keeps unsaved drafts for the current session; use Save before leaving the world.
A saved note can become your objective. Completing an objective archives it as
a notebook entry. **Read all** opens full notifications with category filters;
clearing a category preserves arrivals newer than your confirmation.

Link a note to a saved GPS pin to keep observations with their location.
**Notes** on a map pin opens that location's notebook; **Show on map** returns
to the linked pin. Removing a pin keeps its notes and removes their links.
Location links are saved with the note when you choose **Save note**. Reading
positions are kept when switching apps while the tablet remains open.

**GPS** supports dragging, zooming, following, recentering and heading controls.
Hold the map to drop a private pin, then select it to rename it, navigate to it,
delete it or prepare a message to share it. Sharing prepares a draft in the
selected conversation; review and send it yourself. Switching conversations
restores that conversation's own draft. **Navigate** on a received location card saves
the pin and selects it as your destination together. GPS centers the pin and
opens its actions once surface navigation is available. Reopening the same
message reuses its saved pin during that session.

Choose **Use on watch** to select the watch's Route screen for that destination.
Put away the tablet, then hold your watch key to follow its bearing and distance.
The watch shows the destination name beneath the distance and a relative turn
below it, or an absolute target bearing when your heading is unavailable.
**Scout with drone** opens the flight app with the destination named; deploy and
fly manually. A map pin does not establish the surface height, a safe route,
or an autonomous flight path. Shared locations are reports from their senders.

The **Routes** tab keeps route and trail tools separate from pin actions.
Create a route, add selected pins as steps, then use **Start / Next** to advance
manually. You can step back, reverse a route, remove its last step or fit the
whole route on screen. **Pin my location** records live GPS height;
**Nearest pin** selects the closest saved location by direct distance.
**Find** searches pins; **Choose route** searches routes and **View steps** opens
their itineraries. You can name routes and select any step directly. Distances
are horizontal straight-line estimates, not terrain paths.
**Record trail** records your surface path even with GPS closed;
**Clear trail** removes it, and **Delete route** removes the selected route.
Surface navigation is unavailable in caves or when
the native tracker has no signal. Local pins are separate from the server's
**Places** waypoints, shared notes and rally points; those records do not create
stock GPS icons or change native discovery.

## Messages and notifications

Messages provides **Expedition**, **Tribe** and **Direct** conversations between
compatible clients. Load contacts and select a current recipient for a direct
message. The server checks the current connection, mute state and Tribe access
on each send. **Legacy public** uses the game's ordinary chat and moderation;
the native popup remains available when the tablet cannot take over.

**Share my location** prepares your current surface position as a message.
Check the selected conversation and press **Send** to share it. It sends a
single position, not live tracking; no location is sent merely by opening GPS.
Existing drafts require confirmation before a preset or location replaces them.
Use Unread, Pins and Channel to filter local conversations; Oldest and Latest
move through a transcript. The compose budget shows the managed message's
UTF-8 byte count, so non-ASCII text can reach the limit before 256 characters.
Search accepts several words, with an optional sender-only mode. Use match
arrows to move through results or choose larger reading text. Reuse a message
as a draft without sending it; Clear draft has an undo until you edit, change
conversation or close Messages. Contact refresh keeps the old list visible but
direct sends wait for a current server reply.

For an older direct conversation, **Find current contact** checks successive
contact pages for that exact connection. You can cancel it without losing the
conversation or draft. A reconnected player needs a new contact selection;
matching names do not redirect messages. A lookup never sends your draft.

Notifications on Home and in the shade open their associated app or conversation
and can be dismissed individually. Opening the shade alone does not mark
messages read. Messages become read when their conversation is visible or when
you choose **Mark all read**. Settings controls alerts separately for Expedition,
Tribe and Direct messages.

Messenger retains up to 100 messages in memory and clears them when the game
session changes. It does not provide encryption or delivery/read receipts.
A timeout leaves the result unknown; sending is never retried automatically.

## Settings

Choose Field, Light or Amber colors, brightness, Normal or Large text, reduced
motion, alert volume and Pulse, Chime or Radio tones. **Patrol** and **Camp** use
your chosen brightness and alerts. **Silent** caps brightness at 55% and mutes
alerts while preserving manual settings; messages still arrive. Explicit sound
previews use your chosen volume even when alerts are muted. Preferences are
saved in `BepInEx/config/modstoke.tablet.json`.

Search settings by purpose to jump to a control. Sound explains why alerts are
quiet and offers channel presets. The shade lists all operating modes explicitly,
with shortcuts to Display, Sound, Satellite and Clock.

App changes use a short fade. **Reduced motion** makes these transitions and
the quick-settings drawer settle immediately; dragging the drawer still follows
your pointer. Disabled controls are dimmed and keep their labels visible.

**Standard** is the default HUD mode. Optional **Minimal** hides supported
resource displays while retaining low-resource warnings and alert discs;
**Immersive** hides supported displays more fully while the watch is available.
Unsupported indicators and unknown or third-party minimaps remain visible.
These choices do not change player stats or server rules.

**Satellite** controls a simulated field relay that can remain ready in standby.
Use **SATCOM** in the shade to switch it off or **Reacquire relay** to restart
acquisition. It does not measure sky visibility, provide internet or affect
server access or message delivery. The transceiver choice lasts until the Client
restarts. About includes optional read-only developer information.

## Games

The **Games** app includes two local campaigns with keyboard and touch controls:

- **Tide Runner:** cross three island stages, gather supplies and reach each
  rescue beacon. Use **A/D** or **Left/Right**, **Space** to jump again in midair,
  and **Shift** to dash. Find nine hidden caches among moving platforms,
  collapsing ledges, springs and enemies. Dash through cannonballs to return
  them. A secret grants one water rescue per island; collecting every supply
  earns a shield and score bonus. Checkpoint rallies restore a heart and offer
  an optional timed courier challenge. Ride updrafts and lifts, activate rune
  bridges, break treasure crates and race through airborne rings. **S/Down**
  dives while airborne. The final beacon has a guardian; watch its volley and
  attack during recovery. Earn island medals and race a session-best pace ghost.
- **Signal Breach:** breach three compounds, hack cyan relays and extract after
  defeating the reactor. Use **WASD** or **arrows**, hold **Space** to fire at
  visible enemies, and **Shift** to roll. Use cover against patrols, chargers
  and turrets; find six hidden caches, shields, medkits and rapid-fire upgrades.
  Interrupt enemy windups, roll through a shot to charge a countershot, and
  recover dropped salvage before it expires. Relays retain partial progress
  and release an EMP when hacked. Reactor volleys alternate fans with marked
  safe gaps; its cooling window is the time to attack. Each sector awards
  bonuses for no damage, full clear and complete salvage recovery. Defeat jammer
  officers to free nearby relays, roll through mines to defuse them and use
  completed relays for a one-time repair. Cipher vaults and black-box holdouts
  offer optional rewards. Conveyors, power surges and aimed reactor mortars add
  hazards; consecutive relay downloads can earn a cascade bonus.

Both games use animated character and enemy sprites, illustrated pickups,
terrain and scenery. Movement, attacks and the reactor's phases change their
appearance as you play.

**Enter** starts or resumes; **P** pauses. Touch direction buttons keep moving
until Stop; Signal Breach also offers a touch auto-fire switch. Keyboard input
takes control until another game button is touched. Fast clears, remaining
health, secrets and chained pickups or eliminations improve your score.
Choose **Fullscreen** for a larger play area with keyboard controls. The current
run, score and progress carry over; **Esc** returns to the tablet.
Leaving Games, locking the screen or switching away from the game window
pauses both runs and clears movement. Personal bests use the local field
profile when available. Games have
no network play or rewards in the surrounding game world.

**How to play** pauses the run and opens its manual. Restarting an unfinished
run requires confirmation. The side panel shows ability recharge, timed effects,
active run time and collection requirements; the end report includes hits and
peak combo. Meeting a collection requirement does not bypass the final guardian.

## Tribe and server services

**Tribe** shows membership, announcements, roster and task cards from the current
server. Use Refresh for current membership, page through members and tasks, and
review changes before confirming them. Membership actions include creating a
Tribe, inviting players and leaving; leaders must transfer leadership before
leaving. The invitation view lets you browse eligible players and review incoming
offers to accept or decline.
Task and name drafts show their remaining budget. Copying or clearing an existing
draft asks before replacing it; only a successful matching submission clears it.
Action receipts keep the reviewed target and server result available to read.
**Council** lets permitted members promote or demote officers, remove members,
transfer leadership, and publish or clear announcements. Completed tasks can
be removed after review. **Discuss** prepares a Tribe message about a task;
review it in Messages before sending. Replacing an existing Tribe draft asks
for confirmation. The fuller command editors remain available through **F6**.
Tribe roles do not grant server administration privileges.

**Devices** scans nearby native printers within 12 metres. Select a printer to
pair, browse recipes, review a recipe change or print request, and confirm it.
The app reads server status while a linked device is visible and shows the age
of its observation alongside request receipts. Pairing is temporary; the server
rechecks range, access and the current player. A successful receipt does not
prove a print completed. Refresh status after an uncertain result before
requesting another change. Other machine types are not supported.

**Recipes & plan** retains recipe pages you have visited for the current link.
Sort them, compare two recipes, or estimate resin and nominal duration for
1â20 print invocations. The plan is local: it does not queue production or
promise an output count or completion time. Expired observations remain useful
for planning, but controls require current printer status and server permission.

**Places** manages server waypoints and rally points. **Services** provides
campaigns, Tribe points, travel and facility information. Choose a category and
operation, fill in its details, then request information or review a change.
The response stays alongside the form with its original request, so switching
operations does not present an earlier reply as the new result. Changes have
a separate confirmation step; travel requests do not confirm arrival.
**Companions** manages
server-enabled queues of supported native Get orders for eligible Tribe leaders
and officers. These services depend on the server's configuration and permissions
and do not require managed admission. Corresponding chat commands remain
available to ordinary players.

Server changes require a separate review and confirmation. Editing a draft or
changing sessions invalidates its confirmation. Replies are dated observations,
expire after 30 seconds, and clear when the command session changes. The server
rechecks permission on each request. Requests do not retry or fall back to chat
automatically; after a timeout, read the current state before repeating a change.
Successful persistent changes await the next world save.

Travel receipts do not guarantee arrival or safe terrain; check request status.
Facility information does not unlock doors or grant access. Closing a companion
queue or removing a waiting job does not cancel work already running, and a job
ending does not prove resource delivery. Strict combat remains unavailable in
normal Client settings; installing the package does not enable it.

## Managed admission

Open **Modstoke Client Settings** in the in-game mod menu and enable **Enable managed admission** before connecting. **Server compatibility** shows the configured preference, observed SDK selection, selection ownership, transport state, and whether a restart is required. Selection alone does not establish that a connection has been admitted.

The separate **Latest joining-client admission** section shows the most recent retained joining attempt. Pending means the native package admission exchange is waiting; admitted means local compatibility acceptance was observed; rejected includes the recorded reason. Ended attempts are labeled as history and keep their last observed state. An admitted historical record does not mean you are still connected. A pending historical record does not imply rejection. If no attempt was recorded, the menu makes no admission claim: enrollment can fail before native package admission begins.

Recorded comparisons show required native package IDs, exact required versions, and outcomes such as matched, missing, version mismatch, or inactive. Use **Previous requirements** and **Next requirements** to view eight packages per page. A new latest attempt resets the page. Displayed server text is bounded, with angle brackets replaced by visible alternatives and control or formatting characters replaced by spaces; the SDK compares the original values exactly.

Observations are refreshed by the game thread about twice per second and displayed from a cached immutable snapshot. An unavailable read clears the cached result. The SDK retains up to 32 attempts in process memory, including ended attempts, and loses this history when the game process restarts. This menu shows only the latest retained joining attempt, not a full history browser. Viewing results or changing pages does not change admission policy, install packages, retry admission, or reconnect.

Enable this setting only for managed servers. Leave it disabled when joining or hosting an ordinary vanilla-compatible session. A host using managed admission needs a server integration with a valid requirements document; client-only selection cannot supply that server policy.

The package saves its setting to `BepInEx/config/modstoke.client.cfg` under the SDK-reported game directory. You can create or edit this UTF-8 file while the game is stopped:

```ini
EnableManagedAdmission = true
```

Set the value to `false` to disable participation on the next game start. A missing file defaults to disabled and is not created until a setting is saved. The file accepts one Boolean setting, blank lines, and lines beginning with `#`, up to 1024 bytes. Unknown or duplicate settings, invalid values, and read failures block selection until corrected through the settings menu or by editing the file and restarting. A settings change is accepted only after it is saved successfully; this package owns this file and rewrites it when saving.

Changes made while networking is starting, running, or shutting down are saved for the next game start. The client does not reconnect or retry selection automatically. It respects server requirements selected by another integration and clears only a selection whose current revision it still owns. Disabling this package cannot disable another integration's selection. Shutting down unregisters the menu and settings without withdrawing a live gate.

Admission reports are untrusted client claims about native mods at admission time. They do not prove integrity, authenticate an account, or authorize combat or other gameplay actions. Compatibility plugins are outside this native package report.

## Source layout

Start at [Plugin.cs](Plugin.cs) for startup, frame updates and shutdown.

| Folder | Contents |
| --- | --- |
| [Aircraft](Aircraft) | Aircraft services, cockpit, models, seating and physics. |
| [Spacecraft](Spacecraft) | Spaceflight, cabin seating, physics and destination world. |
| [Drone](Drone) | Drone services, controller models, flight physics and surveys. |
| [Devices](Devices) | Shared input, materials, inventory equipment and printer apps. |
| [Devices/Tablet](Devices/Tablet) | Held tablet, operating system, display and preferences. |
| [Devices/Watch](Devices/Watch) | Wristwatch model, fit, gestures, display and equipment. |
| [Devices/Presence](Devices/Presence) | Shared device presentation, poses and screen transport. |
| [Arcade](Arcade) | Tablet games and sprite loading. |
| [Navigation](Navigation) | GPS, maps, field profiles, markers and notebook tools. |
| [Networking](Networking) | Admission, messaging, contacts and location messages. |
| [Community](Community) | Tribe, companion and server-service apps and providers. |
| [Inventory](Inventory) | Pack, equipment, crafting-mat observations and recipes. |
| [Combat](Combat), [Survival](Survival) | Combat integration and health observations. |
| [UI/Terminal](UI/Terminal) | Terminal state, input and app coordination. |
| [UI/Views](UI/Views) | Terminal screens, widgets and presentation. |
| [Configuration](Configuration) | Client configuration persistence. |

Filenames omit the redundant `Client` prefix; types and namespaces retain their
existing names. Keep related partial files together, including service-specific
networking parts. Assets remain in `Assets/`.

[Modstoke.Client.csproj](Modstoke.Client.csproj) lists source files explicitly.
Preserve entry order when moving files: partial field initializers can depend
on declaration order. Add new source files to that list.

Build against the current loader SDK from this directory:

```powershell
dotnet build Modstoke.Client.csproj --configuration Release "-p:LoaderAssemblyPath=C:\path\to\Modstoke.Loader.dll"
```

The project targets .NET 11. In-game menu and multiplayer behavior require runtime acceptance with the matching loader.

## License

OpenACAI-authored code is licensed under GPL-3.0-or-later. The Client package
includes its license; the Loader distribution retains required third-party notices.
