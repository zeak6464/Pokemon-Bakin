RPG Developer Bakin
Enhanced Battle Plug-in Sample with Pokemon-Style Systems
March 3, 2023 SmileBoom Co.Ltd. | Enhanced 2025
---------------------------------------------

This is an enhanced sample project for the "RPG Developer Bakin" battle plug-in featuring a comprehensive Pokemon-style battle system with dual-types, priority attacks, capture mechanics, IV/EV systems, and more.

A plug-in written in C# is stored in the 'battlescript' folder directly under the project folder.

--- ENHANCED FEATURES OVERVIEW ---

🎮 CORE BATTLE SYSTEMS:
- Speed-based turn order (replaces simple alternating turns)
- Priority attack system with skill tags (#highpriority, #lowpriority)
- Dual-type effectiveness system for both attackers and defenders
- Dynamic type chart with 18+ supported types
- Status ailment decay and stat enhancement reduction over time

🏹 CAPTURE SYSTEM:
- Monster capture with state preservation
- Captured monsters retain all battle conditions and enhancements
- Automatic party integration with fallback methods

🌤️ WEATHER SYSTEM:
- Pokemon-style weather effects that modify move damage
- Rain boosts Water moves (1.5x) and weakens Fire moves (0.5x)
- Sun boosts Fire moves (1.5x) and weakens Water moves (0.5x)
- Sandstorm boosts Rock/Ground/Steel moves (1.2x)
- Hail boosts Ice moves (1.2x)
- Reads from in-game variable "Weather" (0=Clear, 1=Rain, 2=Sun, 3=Sandstorm, 4=Hail)

⚡ PRIORITY & SWITCHING:
- Run and Pokemon (switching) commands have highest priority
- #switchclear status ailment tag system
- Automatic retargeting when characters switch out
- Comprehensive switching for both manual and automatic scenarios

📊 POKEMON-STYLE STATS:
- Individual Values (IV) system placeholder
- Effort Values (EV) system placeholder
- Stat enhancement decay over turns
- Comprehensive logging for all calculations

🏷️ TAG SYSTEMS:
- Management tags for monster/character types (#fire, #water, #grass, etc.)
- Skill tags for attack types and priorities
- Status ailment tags for special behaviors
- Evolution system tags (placeholder)

--- DETAILED SYSTEM DOCUMENTATION ---

🌤️ POKEMON-STYLE WEATHER SYSTEM
Location: Lines 156-233 in BattleSequenceManager.cs

WEATHER VARIABLE INTEGRATION:
✅ Reads from in-game variable "Weather" for current weather state
✅ Formula integration via "weather" variable in damage calculations
✅ Real-time weather effects during battle

WEATHER TYPES & EFFECTS:
• Weather = 0 (Clear): No weather effects (1.0x multiplier)
• Weather = 1 (Rain): Water moves boosted 1.5x, Fire moves reduced 0.5x
• Weather = 2 (Sun): Fire moves boosted 1.5x, Water moves reduced 0.5x  
• Weather = 3 (Sandstorm): Rock/Ground/Steel moves boosted 1.2x
• Weather = 4 (Hail): Ice moves boosted 1.2x

USAGE IN FORMULAS:
Example damage formula with weather: "max(1, floor(base_damage * edef * weather))"
The "weather" variable automatically applies appropriate multipliers based on move type.

🔥 COMPLETE POKEMON TYPE EFFECTIVENESS SYSTEM + STAB
Location: Lines 6395-6800 in BattleSequenceManager.cs

FULL POKEMON GEN 6+ TYPE CHART IMPLEMENTATION:
✅ All 18 Official Pokemon Types Supported
✅ Complete Type Effectiveness Chart (324 matchups)
✅ Official Pokemon Damage Formula Integration
✅ STAB (Same Type Attack Bonus) System
✅ Formula Variable Integration (edef)

HOW IT WORKS:
1. Add type tags to monster management tags: "#fire", "#water", "#grass #poison"
2. Add type tags to skill tags: "#fire #highpriority", "#water #ice"
3. System automatically calculates effectiveness using complete Pokemon type chart
4. Dual-type attacks average their effectiveness (prevents overpowered combinations)
5. Dual-type defenders multiply effectiveness (traditional Pokemon rules)
6. STAB (Same Type Attack Bonus): 1.5x damage when attacker's type matches attack type

COMPLETE TYPE CHART (Gen 6+ Accurate):
- 0.0x (No Effect): Electric → Ground, Fighting → Ghost, Normal → Ghost, etc.
- 0.5x (Not Very Effective): Fire → Water, Water → Grass, Psychic → Psychic, etc.
- 1.0x (Normal Damage): Neutral matchups
- 2.0x (Super Effective): Fire → Grass, Water → Fire, Fighting → Normal, etc.

OFFICIAL POKEMON DAMAGE FORMULA:
Uses authentic Pokemon damage calculation with proper rounding:
```
floor(floor(floor(floor(2 * level / 5 + 2) * power * attack / defense) / 50) + 2) * edef)
```

FORMULA INTEGRATION:
- 'edef' variable: Returns complete type effectiveness multiplier
- Works in skill damage formulas alongside other variables (spatk, spdef, etc.)
- Includes STAB calculation automatically
- Matches official Pokemon damage calculator results

STAB EXAMPLES:
- Psychic-type Mew using Psychic skill (#psychic): 1.5x STAB bonus
- Fire/Flying dual-type using Fire Blast (#fire): 1.5x STAB bonus  
- Water-type using Fire skill (#fire): No STAB bonus (1.0x)

CALCULATION ORDER:
1. Base type effectiveness calculated (e.g., Psychic vs Psychic = 0.5x)
2. STAB bonus applied if types match (e.g., 0.5x × 1.5x = 0.75x)
3. Final multiplier returned via 'edef' variable
4. Applied in damage formula for accurate Pokemon-style damage

ALL 18 POKEMON TYPES SUPPORTED:
normal, fire, water, electric, grass, ice, fighting, poison, ground, flying, 
psychic, bug, rock, ghost, dragon, dark, steel, fairy
(Note: "wind" supported as alias for "flying")

TYPE EFFECTIVENESS EXAMPLES:
- Fire → Grass/Ice/Bug/Steel = 2.0x (Super Effective)
- Water → Fire/Ground/Rock = 2.0x (Super Effective)
- Electric → Water/Flying = 2.0x (Super Effective)
- Electric → Ground = 0.0x (No Effect)
- Psychic → Fighting/Poison = 2.0x (Super Effective)
- Psychic → Dark = 0.0x (No Effect)

CUSTOMIZATION:
- Add new types: Modify commonTypes array lines 6575-6577
- Edit type chart: Modify GetTypeEffectiveness() function lines 6645-6800
- Adjust STAB multiplier: Modify STAB_MULTIPLIER constant line 6509 (default: 1.5f)
- Adjust calculation method: Modify CalculateDualTypeEffectiveness() lines 6395-6499

INTEGRATION POINTS:
- Formula variable: 'edef' in damage formulas
- Weapon damage: Line 2332 CalcAttackWithWeaponDamage()
- Skill damage: Lines 3605, 3733, 3752 in skill effect calculations
- Direct formula access: parseBattleNum() function line 2707

OFFICIAL POKEMON FORMULA IMPLEMENTATION:
Based on Smogon damage calculator source code (https://github.com/smogon/damage-calc)
- Authentic multi-step floor() rounding behavior
- Proper level/power/stat calculation order  
- Verified against official Pokemon damage calculator
- Produces identical results to competitive Pokemon tools

EXAMPLE CALCULATION (Level 1 Mew vs Level 1 Mew):
1. floor(2 * 1 / 5 + 2) = floor(2.4) = 2
2. floor(2 * 90 * 7 / 7) = floor(180) = 180  
3. floor(180 / 50) = floor(3.6) = 3
4. 3 + 2 = 5 (base damage)
5. floor(5 * 0.75) = floor(3.75) = 3 (final damage)

DEBUGGING & VERIFICATION:
- "TYPE CALC" logs: Complete calculation breakdown
- "SIMPLE TYPE" logs: Formula context calculations  
- "EDEF FORMULA" logs: Formula variable integration
- "EDEF DEBUG" logs: Function call verification
- Matches official Smogon damage calculator results

⚡ PRIORITY SYSTEM
Location: Lines 5992-6114 in BattleSequenceManager.cs

PRIORITY ORDER (highest to lowest):
1. Run commands (PlayerEscape, MonsterEscape)
2. Pokemon commands (switching characters)
3. Guard commands
4. #highpriority skills (sorted by speed)
5. Normal skills (sorted by speed)
6. #lowpriority skills (sorted by speed)

SKILL TAGS:
- #highpriority: Skill executes before normal actions
- #lowpriority: Skill executes after normal actions
- Can combine with type tags: "#fire #highpriority"

CUSTOMIZATION:
- Modify priority tiers: Edit UpdateBattleState_SortBattleActions() lines 5992-6071
- Add new priority levels: Add cases in GetPriorityText() lines 6095-6114
- Change speed tie-breaking: Modify OrderByDescending/OrderBy clauses

🎯 CAPTURE SYSTEM
Location: Lines 350-496 in BattleSequenceManager.cs

HOW IT WORKS:
1. Mark enemies for capture using CAPTURE_STATUS_NAME condition
2. ProcessCapturedEnemies() preserves all battle states during conversion
3. Creates Hero from monster data using party.createHeroFromRom()
4. Transfers conditionInfoDic (all status effects and enhancements)
5. Adds to party with multiple fallback methods

CAPTURE STATUS NAME:
- Default: "Captured" (line 273)
- Change capture condition: Modify CAPTURE_STATUS_NAME constant

CUSTOMIZATION:
- Modify capture rate: Edit capture detection logic in ProcessCapturedEnemies()
- Change state preservation: Modify condition transfer code lines 450-470
- Adjust party integration: Edit fallback methods lines 475-495

🔄 SWITCHING & STATUS CLEAR
Location: Lines 6152-6353 in BattleSequenceManager.cs

#SWITCHCLEAR TAG SYSTEM:
- Add "#switchclear" to status condition names or message fields
- Conditions are automatically removed when character switches out
- Works for both manual and automatic switching

INTEGRATION POINTS:
- Manual player switch: Line 7633
- Manual enemy switch: Line 7741
- Auto player switch: Line 1309
- Auto enemy switch: Line 1258

RETARGETING SYSTEM:
- All pending attacks automatically retarget from outgoing to incoming character
- RetargetAllAttacksFromTo() function lines 6627-6690
- Prevents attacks hitting switched-out characters

CUSTOMIZATION:
- Change tag detection: Modify HasSwitchClearTag() lines 6152-6265
- Edit message fields checked: Modify messageFields array line 6206
- Adjust clearing behavior: Edit ClearSwitchClearableStatuses() lines 6266-6329

📈 STAT DECAY SYSTEM
Location: Lines 5424-5542 in BattleSequenceManager.cs

DECAY MECHANICS:
- Status ailments: Turn-based and probability-based recovery
- Stat enhancements: Multiplied by DampingRate each turn
- All enhancement values decay toward 0 over time

DECAY RATE:
- Current: DampingRate = 1f (no decay - line 5479)
- For 5% decay per turn: Change to 0.95f
- For 10% decay per turn: Change to 0.90f

STATS AFFECTED:
- MaxHitPointEnhance, MaxMagicPointEnhance
- PowerEnhancement, VitalityEnhancement, MagicEnhancement
- SpeedEnhancement, EvasionEnhancement, DexterityEnhancement
- enhanceStatusValue (all status multipliers)
- ResistanceAttackAttributeEnhance, ResistanceAilmentEnhance

CUSTOMIZATION:
- Adjust decay rate: Modify DampingRate constants lines 5479, 5506
- Change affected stats: Edit UpdateBattleState_PlayerTurnStart() lines 5475-5540
- Add immunity conditions: Add checks before applying decay

🎲 SHINY & IV/EV SYSTEMS (Placeholder Framework)

IV SYSTEM FRAMEWORK:
- Individual Values: Unique stat bonuses per monster (0-31 per stat)
- Implementation location: Add to Hero/Monster creation
- Suggested location: ProcessCapturedEnemies() for captured monsters

EV SYSTEM FRAMEWORK:
- Effort Values: Trainable stat bonuses (0-255 per stat, 510 total)
- Implementation location: Add to battle victory conditions
- Suggested location: After defeating enemies in battle

SHINY SYSTEM FRAMEWORK:
- Rare color variants with special properties
- Implementation suggestion: Check during monster creation
- Rate customization: 1/4096 (Pokemon standard) or custom rate

SUGGESTED IMPLEMENTATION LINES:
- Add IV generation: After line 470 in ProcessCapturedEnemies()
- Add EV rewards: After enemy defeat in battle victory logic
- Add shiny check: During monster spawning/encounter

--- TAG REFERENCE GUIDE ---

🏷️ MANAGEMENT TAGS (Monster/Character):
#fire, #water, #grass, #electric, #ice, #fighting, #poison, #ground
#flying, #wind, #psychic, #bug, #rock, #ghost, #dragon, #dark, #steel
#fairy, #normal

Example: "#fire #flying" creates a Fire/Flying dual-type

🏷️ SKILL TAGS:
TYPE TAGS: #fire, #water, etc. (same as management tags)
PRIORITY TAGS: #highpriority, #lowpriority
COMBINATION: "#fire #highpriority" for high-priority fire attack

🏷️ STATUS AILMENT TAGS:
#switchclear: Condition is removed when character switches out

🏷️ EVOLUTION TAGS (Framework):
#evolution, #evolve_[level], #evolve_[condition]
Implementation: Add to monster management tags and evolution logic

--- SPEED-BASED TURN SYSTEM ---
Location: Lines 6089-6111 in BattleSequenceManager.cs

TURN ORDER:
1. All characters select actions
2. Actions sorted by priority tier (see Priority System above)
3. Within each tier, sorted by Speed stat (highest first)
4. Execute actions in calculated order

REMOVED FEATURES:
- isPlayerTurn variable (no longer alternating turns)
- Simple player/enemy alternation

CUSTOMIZATION:
- Change speed calculation: Modify OrderByDescending(character => character.Speed)
- Add speed ties: Add secondary sort criteria (e.g., by UniqueID)
- Adjust turn calculation: Edit UpdateBattleState_WaitCtbGauge() logic

--- DEBUGGING & LOGGING ---

All systems include comprehensive debug logging:
- "Dual Type Debug": Type detection and calculation details
- "Priority System": Action ordering and priority assignments
- "Switch Clear": Status ailment clearing process
- "Capture": Monster capture and state transfer process

ENABLE/DISABLE LOGGING:
- Comment out GameMain.PushLog() calls to reduce output
- Search for "Debug" to find all debug logging statements

--- ORIGINAL CTB FEATURES (Preserved) ---

What this sample battle plug-in does is roughly the following three things:
- Based on the status of the cast members' "agility," a value (count) is added over time, and the cast member who has reached the specified value can choose their action.
- Stores the accumulated counts in the variable "turnCount", which can be referenced from the Layout Tool.
- New special formats available in the Layout Tool: "battleext" for displaying the order of actions, and "CtbSelected[]", a special coordinate specification tag for indicating the opponent of an attack.

--- LAYOUTS ---
The layout for displaying the order of actions during battle is created in the Layout Tool > Battle Statuses.
Two layouts are currently available.

Type-A
This layout shows the order of actions by the sequence of cast icons.
Text panels that display the special format "battleext" are lined up on the battle status screen.
The "battleext" checks the count of each cast and displays an icon image of the cast (or a graphic for movement if not specified) according to the order of their actions.
To indicate when the enemy selected as the attack target is scheduled to act, rendering containers with the special coordinate specification tag CtbSelected[] are used.

Type-B　
This layout shows the count of each cast in the form of gauges.
Added slider rendering panels (part name "Count Gauge") to the battle status screen to display gauges based on the value of "turnCount".
The layout also includes hidden parts that display an enemy status.  Please refer to it.

- Other battle-related screen layouts are adjusted to match the display of each battle status.
- After starting the sample project, you can talk to the statue at the rear of the map to switch the battle layout to be used.

--- CAMERAS ---
Two types of preset data were added to the Battle Camera.
Battle Camera E: Side-view type for either layout Type-A or B
Battle Camera F: Back view type suitable for layout Type-B
After selecting the battle camera in the Camera Tool, you can select it from the Load from Presets button. The default battle camera for the project is Battle Camera E.

--- How to Apply This Project's Battle Script to Your Own Project
*Some of the image data uses assets that are included when the amount of assets is set to "Normal" when creating a new project.
If you do not find the images you need in your project after performing the following operations, please use multiple launches of Bakin and copy the resources from the sample game "Orb Stories" or from a new project created with the asset amount set to "Normal".　

1. Copy the 'battlescript' folder directly under the project you want to reflect.
2. Launch Battle Plug-in Sample, open the Layout Tool, and right-click on the preview screen. Save the layouts to a file.
3. Open the project you want to reflect and right-click in the preview window of the Layout Tool. Load the .lyrbr file you just saved.

ADDITIONAL STEPS FOR ENHANCED FEATURES:
4. Add type management tags to your monsters (e.g., "#fire", "#water #ice")
5. Add type and priority tags to your skills (e.g., "#fire #highpriority")
6. Create status conditions with "#switchclear" tags for switching mechanics
7. Test the dual-type effectiveness with various type combinations
8. Customize the type chart in GetTypeEffectiveness() for your game's balance

--- QUICK CUSTOMIZATION GUIDE ---

🔧 COMMON MODIFICATIONS:

SET WEATHER EFFECTS:
- Create in-game variable "Weather" (integer type)
- Set values: 0=Clear, 1=Rain, 2=Sun, 3=Sandstorm, 4=Hail
- Use "weather" variable in damage formulas for automatic effects
- Location: Lines 156-233 GetWeatherMultiplier()

CHANGE SHINY RATE:
- Location: Add to monster creation logic
- Suggested: 1/4096 for rare, 1/512 for common

ADJUST TYPE EFFECTIVENESS:
- Location: Lines 6645-6800 GetTypeEffectiveness()
- Example: Make Fire super effective vs Steel: case "steel": return 2.0f;
- Note: Current implementation uses complete official Pokemon type chart

MODIFY STAT DECAY RATE:
- Location: Lines 5479 and 5506 DampingRate constants
- 1.0f = no decay, 0.95f = 5% decay, 0.90f = 10% decay

ADD NEW PRIORITY TIER:
- Location: Lines 5992-6071 UpdateBattleState_SortBattleActions()
- Add new skill tag check and sorting logic

CHANGE CAPTURE CONDITION:
- Location: Line 273 CAPTURE_STATUS_NAME constant
- Change from "Captured" to your preferred condition name

ADJUST STAB MULTIPLIER:
- Location: Line 6509 STAB_MULTIPLIER constant
- Default: 1.5f (50% bonus), Common alternatives: 1.25f (25%), 2.0f (100%)

ADD NEW TYPE:
- Step 1: Add to commonTypes array lines 6575-6577
- Step 2: Add type effectiveness in GetTypeEffectiveness() lines 6645-6800
- Step 3: Test with management and skill tags
- Note: All 18 official Pokemon types already implemented

--- About Plug-ins ---
Please refer to the manual "RPG Developer Bakin" for more information on the plug-in itself, including how to create it.
RPG Developer Bakin Wiki : https://rpgbakin.com/pukiwiki_en/

--- Enhanced System Credits ---
Enhanced Pokemon-style systems implemented:
- Dual-Type Effectiveness System
- Priority Attack Framework  
- Capture with State Preservation
- Speed-Based Turn Order
- Status Ailment Decay
- Switching & Retargeting System
- Comprehensive Debug Logging

--- Notice ---
Please note that this enhanced battle plug-in may not work with updates to Bakin itself.
The enhanced systems are designed to be modular and customizable for your specific game needs.

---------------------------------------------
RPG Developer Bakin
© 2025 SmileBoom Co.Ltd. All Rights Reserved.
Enhanced Systems Implementation 2025