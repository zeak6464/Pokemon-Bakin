# Pokemon Bakin

A comprehensive Pokemon remake project built using **RPG Developer Bakin** with custom battle systems, complete Pokemon data, and enhanced gameplay mechanics.

## 🎮 Overview

This project recreates the classic Pokemon experience within RPG Developer Bakin, featuring:

- **Complete Pokemon Data**: Full roster of Pokemon with authentic stats, moves, types, and evolution data
- **Custom Battle System**: Advanced C# battle plugin with turn-based mechanics and visual effects
- **Authentic Maps**: Recreation of iconic Pokemon locations (Pallet Town, Viridian City, Routes, etc.)
- **Pokemon Resources**: Complete sprite sets, battle graphics, and character assets
- **Custom Scripts**: Enhanced gameplay mechanics and Pokemon-specific features

## ✨ Features

### Pokemon 

### Battle System
- **Custom C# Battle Engine** (`battlescript/` folder)
- **Turn-based combat** with speed-based action order
- **3D battle environments** with camera controls
- **Visual effects** and animations
- **Status conditions** and battle mechanics
- **Party management** and Pokemon switching

### Maps & Locations
- Pallet Town (starting location)
- Viridian City and Viridian Forest
- Route connections and gateways
- Pokemon Centers and key buildings
- Battle environments and encounter areas

### Resources
- **302 Battle Sprites** (`Poke resources/Battlers/`)
- **151 Overworld Sprites** (`Poke resources/Characters/`)
- **Pokemon Icons** and UI elements
- **Custom fonts** and visual assets
- **Sound effects** and music
- **151 Pokemon** with complete stat data (`pokemonstats.csv`)
- **Authentic movesets** and abilities (`moves.csv`)
- **Nature system** affecting Pokemon growth (`natures.csv`)
- **Experience types** and leveling curves
- **Evolution mechanics** including level and item-based evolution

## 🛠️ Technical Details

### Built With
- **RPG Developer Bakin** - Main game engine
- **C# .NET Framework** - Custom battle system

### Custom Battle System
The project includes a sophisticated battle plugin written in C#:

- `BattleSequenceManager.cs` - Core battle flow management
- `BattleActor.cs` - Character animation and 3D positioning
- `BattleViewer3D.cs` - 3D battle visualization
- `BattleEventController.cs` - Event handling and scripting
- `CommandTargetSelector.cs` - Target selection mechanics

### Key Components
```
Pokemon-Bakin/
├── battlescript/          # Custom C# battle system
├── map/                   # Game world maps
├── Poke resources/        # Pokemon sprites and data
├── script/               # Additional game scripts
├── res/                  # Game resources and assets
└── savedata/             # Game save files
```

## 🚀 Getting Started

### Prerequisites
- RPG Developer Bakin (licensed version)
- Windows 10/11
- .NET Framework support

### Installation
1. Clone this repository
2. Open the project in RPG Developer Bakin
3. The custom battle system will be automatically loaded
4. Run the game to start your Pokemon adventure

### Development
To modify the battle system:
1. Open `battlescript/Yukar Battle Engine.sln` in Visual Studio
2. Make your changes to the C# source files
3. Build the project to generate the updated plugin
4. Test changes in RPG Developer Bakin

## 📊 Pokemon Data

- **Stats**: HP, Attack, Defense, Special Attack, Special Defense, Speed - WIP
- **Types**: All 17 Pokemon types with effectiveness calculations - Done
- **Moves**: Complete moveset database with power, accuracy, and effects - WIP
- **Evolution**: Level-based and item-based evolution chains - WIP
- **Experience**: Multiple experience growth curves (Fast, Medium Fast, Medium Slow, Slow) - WIP

## 🎯 Game Features

### Core Gameplay
- Explore the Pokemon world with authentic locations
- Catch and train Pokemon with original mechanics
- Battle wild Pokemon and trainers
- Evolve Pokemon through various methods
- Manage your Pokemon party and PC storage

### Enhanced Features
- Custom battle animations and effects
- 3D battle environments
- Advanced AI for trainer battles
- Quality of life improvements
- Modern UI elements while maintaining classic feel

## 🤝 Contributing

This project welcomes contributions! Areas where you can help:

- Adding new Pokemon regions and maps
- Enhancing the battle system
- Improving visual effects and animations
- Bug fixes and optimizations
- Documentation improvements

## 📝 License

This project is a fan-made recreation for educational and entertainment purposes. Pokemon is a trademark of Nintendo/Game Freak/Creatures Inc.

## 🙏 Acknowledgments

- **SmileBoom Co.Ltd.** - RPG Developer Bakin engine
- **Nintendo/Game Freak** - Original Pokemon games and concepts
- **Pokemon Community** - Sprite resources and data compilation

---

**Note**: This is a fan project and is not affiliated with or endorsed by Nintendo, Game Freak, or The Pokemon Company.
