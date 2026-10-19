# NRSVault Mini

**Build and look after a complete, correctly headered NES ROM collection based on
NewRisingSun's NES 2.0 database (`nes20db.xml`).**

<!-- Screenshot: main window -->

NRSVault Mini is a free Windows app. You point it at a folder of NES ROMs and it:

- tells you which ROMs from the database you have, which are missing, and which have problems
- adds the correct **NES 2.0 header** to every ROM
- names each file and puts it in the folder the database says it belongs in
- searches your other folders, even huge messy ones, for ROMs you are missing, and copies them in
- uses magic to peer deep inside headered and non-headered files to determine matches
- unknown rom folders can be raw headered or headerless files or packed in zip/7z/rar archives
- complete "point and shoot", no need to get your hands dirty to prep files for analysis

You don't need RomVault or any other ROM manager either. NRSVault Mini can build and maintain the
whole collection by itself.

<img width="1671" height="941" alt="Eggmans_NRSVault_banner" src="https://github.com/user-attachments/assets/408d00fe-b406-4638-9c18-a1be45fd79b5" />

---

## Contents

- [Why headers matter](#why-headers-matter)
- [What you need](#what-you-need)
- [Download and install](#download-and-install)
- [Step-by-step guide](#step-by-step-guide)
- [Understanding the main window](#understanding-the-main-window)
- [What the colours and statuses mean](#what-the-colours-and-statuses-mean)
- [Options explained](#options-explained)
- [When a new nes20db.xml comes out](#when-a-new-nes20dbxml-comes-out)
- [Questions and troubleshooting](#questions-and-troubleshooting)
- [Advanced: making a DAT for RomVault](#advanced-making-a-dat-for-romvault)
- [Building from source](#building-from-source)
- [Credits](#credits)
- [Contributors](#contributors)
- [License](#license)

---

## Why headers matter

An NES ROM file is the data from the chips on the cartridge: PRG (program) and CHR
(graphics). On its own that data is just a pile of bytes. An emulator also needs to know
*how* the cartridge was built: which mapper chip it used, how the screen mirrors, whether it
has battery-backed saves, whether it's a Famicom, NES, PAL or Vs. System game, and so on.

That information lives in a 16-byte **header** at the start of the file. **NES 2.0** is the
modern header format, created by NewRisingSun, and it describes the hardware far more
precisely than the old iNES format.

- **No header:** most emulators can't run the game at all.
- **Old or wrong header:** the game may glitch, crash or refuse to start.
- **Correct NES 2.0 header:** the emulator knows exactly what hardware to emulate.

NewRisingSun keeps a database, `nes20db.xml`, describing more than 21,000 NES, Famicom and
related ROMs: their correct hardware settings, their hashes (fingerprints) and where each
one belongs in a folder structure. NRSVault Mini reads that database and makes your files
match it.

---

## What you need

- **Windows 10 or 11, 64-bit.** Nothing else to install: the app is a single `.exe`.
- **nes20db.xml.** A copy comes with the download (see [Credits](#credits) for where the
  original comes from).
- **Your NES ROMs.** Headered, headerless, UNIF (`.unf`), or inside `.zip`, `.7z` or
  `.rar` archives are all fine.
- **Free disk space** for the finished collection (a near-complete set is roughly 15–20 GB).

---

## Download and install

1. Open the [**Releases**](../../releases) page and download the latest
   `NRSVaultMini-vX.Y.Z-win-x64.zip`.
2. Unzip it anywhere you like, for example `C:\Tools\NRSVaultMini\`. You'll get:
   - `NRSVaultMini.exe`: the app
   - `nes20db.xml`: the database (keep it next to the exe and the app finds it automatically)
3. Double-click `NRSVaultMini.exe`.

> **Windows SmartScreen** may say "Windows protected your PC" because the app isn't
> code-signed. Click **More info**, then **Run anyway**.

The app stores its settings and its scan cache in `%LocalAppData%\NRSVaultMini`. Nothing
is written to the registry.

---

## Step-by-step guide

### Step 1: Decide where your collection will live

The **collection folder** is where the finished, organised ROM set will be kept.
NRSVault Mini rearranges and rewrites files in this folder, so:

- **Starting from scratch?** Create a new, empty folder, for example
  `D:\NES 2.0 Collection`. Your existing ROMs will be pulled in from
  [source folders](#step-5-find-missing-roms-in-your-other-folders) in Step 5.
- **Already have a set laid out like nes20db?** Use that folder.

> **Tip:** before using an existing folder for the first time, make a backup copy of it, or
> tick **Back up originals to** (see [Options](#options-explained)).

Keep the collection folder **uncompressed** (no zips). The app needs loose files so it can
update headers in place. Any archives it finds there are unpacked automatically.

### Step 2: Start the app and load the database

1. Click **…** next to **Collection folder** and choose your folder from Step 1.
2. If `nes20db.xml` sits next to the exe, it's already loaded. The log at the bottom says
   *"Loaded nes20db.xml dated …"*. If not, click **…** next to **nes20db.xml**, choose the
   file, and press **Load DB**.

The left sidebar now shows the database date and how many games and ROM files it describes.



### Step 3: Scan your collection

Press **Scan**. The app reads every file in the collection folder and identifies each one
by its contents, not its name, so badly named files are still recognised.

The first scan of a full collection takes a minute or so. Later scans are almost instant,
because only new or changed files are read again.

### Step 4: Look at the results

- The **sidebar** gives totals: verified OK, missing, fixable, and more. Click a coloured
  count to show just those ROMs.
- The **Collection** tab lists every ROM from the database in its folder tree. Click **▸**
  to open a folder, or use **Expand all**.
  - **Green** rows are perfect.
  - **Orange** rows need attention.
  - **Red** rows couldn't be read.
- Click any ROM to see its details on the right: what's wrong, the header it should have,
  the header it has now, and its hashes.
- Use the search box (name, path, CRC32 or SHA1) and the filter list to narrow things down.

<img width="1403" height="972" alt="image" src="https://github.com/user-attachments/assets/09c47002-f8ad-4378-9760-03b10895871d" />

### Step 5: Find missing ROMs in your other folders

If you have other folders full of NES ROMs (downloads, old sets, "unsorted" dumps), let the
app search them for anything your collection is missing.

1. Open the **Source folders** tab.
2. Click **Add folder…** and pick one or more folders. Sub-folders are searched too.
3. Press **Scan sources**.

The app looks inside every file and every `.zip` / `.7z` / `.rar` for ROM data that matches
the database. It doesn't matter what the files are called, whether they have a header, or
whether the header is wrong.

- ROMs that would fill a gap are listed, and show as **In source folder** in the
  Collection tab.
- **Source folders are only read, never changed.** Matching ROMs are *copied* into your
  collection.
- Hundreds of thousands of files are fine. Only files of a size that could match are read,
  and the results are remembered. If you cancel, the next scan continues where it left off.

<img width="827" height="358" alt="image" src="https://github.com/user-attachments/assets/63b2fd77-e362-4969-80e8-fdd22a060ece" />

### Step 6: Do a trial run

**TRIAL RUN** is ticked by default, and while it's ticked nothing is changed.

1. Make sure **TRIAL RUN (no files changed)** is ticked.
2. Press **Fix / Rebuild (trial)**.
3. Read the **Log**. Every line says what *would* happen:
   - `WRITE`: a ROM would be written with the correct header (copied from your collection
     or from a source folder)
   - `MOVE` / `DUPE` / `UNKNOWN`: a file would be moved out of the way
   - `RMDIR`: an empty folder would be removed

   A summary line at the end gives the totals.

Use **Export log…** if you want to keep or study the list.

### Step 7: Apply the changes

1. Optionally tick **Back up originals to** and choose a folder **outside** your collection.
   Every file is copied there before it's overwritten or removed.
2. **Untick TRIAL RUN.**
3. Press **Fix / Rebuild** and confirm the summary.

When it finishes, the app scans again automatically. Rows that were fixed turn green. Rows
that are still orange are usually **Missing**, meaning you don't have that ROM anywhere yet.

You can repeat Steps 5–7 whenever you find more ROMs.

### Step 8: Play

Point your emulator at the collection folder. Every file now has the correct NES 2.0
header, so Mesen, FCEUX, puNES, Nestopia UE, RetroArch cores, MiSTer and others can run them
as intended.

---

## Understanding the main window

<!-- Screenshot: annotated main window -->

| Area | What it's for |
|---|---|
| **Top bar** | Folders, the main buttons (**Scan**, **Scan sources**, **Fix / Rebuild**, …) and the options |
| **Left sidebar** | Totals for the database, the collection, files on disk and source folders. Click a coloured count to filter |
| **Collection tab** | Every ROM in the database in its folder tree, with status, size, CRC32, SHA1 and mapper. Click a column header to sort |
| **Source folders tab** | Your source folders and the ROMs found in them |
| **Unknown & surplus files tab** | Files in your collection that aren't in the database, or are extra copies |
| **Details panel (right)** | Everything about the selected ROM or folder |
| **Log (bottom)** | What the app did, or would do in a trial run |
| **Status bar** | Progress of the current job. **Cancel** stops it |

---

## What the colours and statuses mean

| Status | Colour | Meaning | Fixed by Fix / Rebuild? |
|---|---|---|---|
| **OK** | 🟩 Green | Right ROM, exact NES 2.0 header, right name and folder | Nothing to do |
| **Bad header** | 🟧 Orange | Right ROM, but the header is old (iNES 1.0) or wrong | ✔ header rewritten |
| **No header** | 🟧 Orange | Right ROM, but it has no header | ✔ header added |
| **UNIF** | 🟧 Orange | Right ROM in the old UNIF format | ✔ converted to NES 2.0 |
| **Wrong name case** | 🟧 Orange | Name differs only in capital letters | ✔ renamed |
| **Copy available** | 🟧 Orange | The database wants this ROM in more than one place and you have one of them | ✔ copied |
| **Misplaced** | 🟧 Orange | You have it, but in the wrong folder or under the wrong name | ✔ moved |
| **In source folder** | 🟧 Orange | Not in your collection, but found in a source folder | ✔ copied in |
| **Wrong file** | 🟧 Orange | A different file is sitting where this ROM should be | ✔ if the right ROM is found elsewhere |
| **Extra data** | 🟧 Orange | Right ROM, but with extra junk bytes at the end (an overdump) | ✔ only if a clean copy is found elsewhere. The app never trims files |
| **Missing** | 🟧 Orange | Not found anywhere yet | Add more source folders |
| **Error** | 🟥 Red | The file couldn't be read | Check the file or disk |

Folder rows show `OK / total` and take the colour of their worst ROM.

### The `_Unknown` and `_Duplicates` folders

During a fix, the app tidies the collection folder:

- **`_Unknown\`** holds files that aren't in the database. They're moved here, never
  deleted, and keep their original sub-folders.
- **`_Duplicates\`** holds extra copies of ROMs you already have in the right place.

Look through these folders and delete what you don't want.

---

## Options explained

| Option | What it does | Default |
|---|---|---|
| **TRIAL RUN** | Only logs what would happen; changes nothing | On |
| **Rebuild to XML structure** | Copies and moves ROMs to the exact names and folders in the database. Untick it to only fix headers of files already in the right place | On |
| **Unknown → _Unknown** | Moves files that aren't in the database into `_Unknown` | On |
| **Surplus copies → _Duplicates** | Moves extra copies into `_Duplicates` | On |
| **Remove empty folders** | Deletes folders left empty after moving files | On |
| **Fill from source folders** | Lets Fix / Rebuild copy ROMs from your source folders | On |
| **Back up originals to** | Copies every file before it's overwritten or removed | Off |

Settings are remembered between sessions.

---

## When a new nes20db.xml comes out

NewRisingSun updates the database from time to time with new dumps, corrections and better
header information.

1. Download the new `nes20db.xml` (see [Credits](#credits)) and replace the copy next to the
   exe, or choose it with **…** and **Load DB**.
2. Press **Scan**. ROMs whose header details changed show as **Bad header**, and new
   entries show as **Missing** or **In source folder**.
3. Optionally press **Scan sources** to look for the new entries.
4. Do a trial run, then **Fix / Rebuild**.

Because the collection is kept uncompressed, updating headers is quick.

---

## Questions and troubleshooting

**Will it ever delete my ROMs?**
No ROM is ever thrown away. Files that don't belong are moved to `_Unknown` or
`_Duplicates`. A file is only overwritten when it's replaced by the correct version of the
same ROM, and **Back up originals to** keeps a copy of it first. Source folders are never
touched.

**Why are there no SHA1 values for missing ROMs?**
The database only knows the hashes of the ROM data *without* a header. The CRC32 of the
finished headered file can be worked out in advance, but the SHA1 can only be calculated
once the file actually exists. Missing ROMs show their expected size and CRC32; the SHA1
appears once the ROM is verified.

**Some file names have a strange colon in them, like `1999꞉ …`.**
Windows doesn't allow `:` in file names, so the database uses a look-alike character (꞉).
That's expected.

**Can I keep my collection zipped?**
Keep the app's collection folder uncompressed so headers can be fixed quickly. If you want
a zipped copy to share or use elsewhere, see the
[DAT section](#advanced-making-a-dat-for-romvault).

**The first source scan is slow.**
The first time, every file of a possible ROM size has to be read; on a slow hard drive with
hundreds of thousands of files that can take a while. You can cancel at any time: progress
is saved, and later scans only read new or changed files.

**Where are the settings and cache?**
`%LocalAppData%\NRSVaultMini`. Delete that folder to reset the app completely.

**Something went wrong. How do I report it?**
Use **Export log…** and **Export report…** and attach them to an
[issue](../../issues).

---

## Advanced: making a DAT for RomVault

You don't need this to use the app. It's for people who manage ROMs with RomVault and want
to add this collection to it, or share it with others.

**Generate DAT…** writes a RomVault-compatible DAT of your **verified** collection: every
ROM's headered size, CRC32 and SHA1, in the database's folder layout. RomVault then builds
the set as one zip per game (`Licensed Japan\1942.zip`). NES emulators run headered ROMs
from zips without any problem.

- The DAT name and author can be changed in the top bar.
- Tick **DAT: include unverified** to also list ROMs you don't have yet (size and CRC32
  only, marked unverified).
- All of the original nes20db information for each game is kept inside the DAT, so the
  DAT alone records how every header was made.

---

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
git clone https://github.com/Eggmansworld/Eggmans_NRSVault_Mini.git
cd Eggmans_NRSVault_Mini
dotnet build NRSVaultMini.sln
dotnet publish src/NRSVaultMini -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

| Folder | Contents |
|---|---|
| `src/NRSVaultMini.Core` | Database reader, NES 2.0 header builder, scanners, matching, fix planner, DAT writer |
| `src/NRSVaultMini` | The Windows app ([Avalonia](https://avaloniaui.net/)) |
| `tools/NRSVaultMini.Check` | Command-line test tool (`headers`, `scan`, `fix`, `dat`, `--src <folder>`) |
| `tools/NRSVaultMini.Shot` | Renders the app window to PNG for visual checks |
| `files/nes20db.xml` | The bundled copy of NewRisingSun's database |

---

## Credits

**NewRisingSun** created the NES 2.0 header format and maintains `nes20db.xml`, the database
this whole app is built on. A copy is bundled here for convenience, but the original and
all of its updates come from NewRisingSun's thread on the NESdev forums:

**➡ [NES 2.0 XML Database – NESdev Forums](https://forums.nesdev.org/viewtopic.php?f=3&t=19940)**

Please visit the thread to get the latest version, and to thank and support NewRisingSun
for years of work documenting NES hardware. `nes20db.xml` remains NewRisingSun's work and
isn't covered by this project's license.

- NES 2.0 format reference: [nesdev.org/wiki/NES_2.0](https://www.nesdev.org/wiki/NES_2.0)
  and [NES 2.0 submappers](https://www.nesdev.org/wiki/NES_2.0_submappers)
- **Kitrinx** wrote [NES_Header_Repair](https://github.com/Kitrinx/NES_Header_Repair). Its
  header-building logic was ported into this app, and it produces identical headers for
  every database entry.
- Built with [Avalonia UI](https://avaloniaui.net/) and
  [SharpCompress](https://github.com/adamhathcock/sharpcompress).

## Contributors

- **Eggman** ([Eggmansworld](https://github.com/Eggmansworld)): creator, design and
  direction, testing, documentation
- **Claude** ([Anthropic](https://www.anthropic.com/claude)): an AI assistant. Wrote the
  application code, the header port and its verification, the test tools and the original
  README, working with Eggman in Claude Code

## License

Original source code, scripts, tooling, and hand-authored documentation and metadata in this repository are licensed under the MIT License.

Archived game data, binaries, firmware, media assets, and other third-party materials are not covered by the MIT License and remain the property of their respective copyright holders.

See the LICENSE and NOTICE files for full details and scope clarification.
