#define WINDOWS
#define ENABLE_TEST
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Yukar.Common;
using Yukar.Common.GameData;
using Yukar.Engine;
using static Yukar.Engine.BattleEnum;
using AttackAttributeType = System.Guid;
using BattleCommand = Yukar.Common.Rom.BattleCommand;
using Resource = Yukar.Common.Resource;
using Rom = Yukar.Common.Rom;

namespace Yukar.Battle
{
    /// <summary>
    /// BattleCharacterBase の拡張メソッド定義用クラス
    /// Class for defining extension methods of BattleCharacterBase
    /// </summary>
    static class BattleCharacterBaseEx
    {
        /// <summary>
        /// Speedが0だとゲージが進行しないので、+1した値を返すための拡張メソッド
        /// If the speed is 0, the gauge will not progress, so an extension method to return the value with +1
        /// </summary>
        /// <param name="b"></param>
        /// <returns></returns>
        public static int SpeedPlusOne(this BattleCharacterBase b)
        {
            return b.Speed + 1;
        }
    }

    /// <summary>
    /// バトル全般の進行管理を行うクラス
    /// A class that manages the progress of battles in general.
    /// </summary>
    public class BattleSequenceManager : BattleSequenceManagerBase
    {
        public class ExBattlePlayerData : BattlePlayerData
        {
            public ExBattlePlayerData()
            {
                turnGauge = GameMain.instance.mapScene.GetRandom(0.25f, 0f);
            }

            public float turnGauge;

            internal float GetNormalizedTurn(int turn)
            {
                return GetNormalizedTurnImpl(turn, IsDeadCondition() ? 0.001f : this.SpeedPlusOne(), turnGauge);
            }

            internal static float GetNormalizedTurnImpl(int turn, float speed, float turnGauge)
            {
                return (turn - turnGauge) / speed;
            }
        }

        public class ExBattleEnemyData : BattleEnemyData
        {
            public ExBattleEnemyData()
            {
                turnGauge = GameMain.instance.mapScene.GetRandom(0.25f, 0f);
            }

            public float turnGauge;

            internal float GetNormalizedTurn(int turn)
            {
                return ExBattlePlayerData.GetNormalizedTurnImpl(turn, IsDeadCondition() ? 0.001f : this.SpeedPlusOne(), turnGauge);
            }
        }

        // --- Pokémon-style multiplicative type effectiveness ---
        // Convert Bakin's compatibility percent to multiplier
        private static float ToMultiplier(int percent) => percent / 100f;

        // Get type effectiveness factor for one defender type vs attack attribute
        private static float GetSingleTypeFactor(Guid attackAttr, Guid defenderType, Catalog catalog)
        {
            if (defenderType == Guid.Empty) return 1f;
            
            var defAttr = catalog.getItemFromGuid<Yukar.Common.Rom.Attribute>(defenderType);
            if (defAttr?.affinityList == null) return 1f;

            // Find compatibility entry for this attack attribute
            var match = defAttr.affinityList.FirstOrDefault(a => a.attribute == attackAttr);
            var percent = (match != null) ? match.value : 100; // default neutral = 100%
            return ToMultiplier(percent); // 50→0.5, -100→2.0, 0→0 (immune)
        }

        // Extract defender's "types" (main cast + subclass/side job)
        private static IEnumerable<Guid> GetDefenderTypes(BattleCharacterBase target)
        {
            if (target is BattlePlayerData pl)
            {
                // Main job/cast GUID (the cast IS the attribute)
                var main = pl.player?.jobCast?.rom?.guId ?? Guid.Empty;
                // Side job GUID (dual typing)
                var sub = pl.player?.sideJobCast?.rom?.guId ?? Guid.Empty;
                
                if (main != Guid.Empty) yield return main;
                if (sub != Guid.Empty && sub != main) yield return sub;
            }
            else if (target is BattleEnemyData en)
            {
                // Enemy's main cast type
                var main = en.monsterGameData?.rom?.guId ?? Guid.Empty;
                // Enemy's side job (if configured for dual typing)
                var sub = en.monsterGameData?.sideJobCast?.rom?.guId ?? Guid.Empty;
                
                if (main != Guid.Empty) yield return main;
                if (sub != Guid.Empty && sub != main) yield return sub;
            }
        }

        // Pokémon-style multiplier: product of per-type factors
        private static float GetPokemonTypeMultiplier(Guid attackAttr, BattleCharacterBase target, Catalog catalog)
        {
            Console.WriteLine($"POKEMON: Starting calc - Attack={attackAttr}, Target={target.Name}");
            
            var types = GetDefenderTypes(target).ToArray();
            Console.WriteLine($"POKEMON: Found {types.Length} defender types");
            
            if (types.Length == 0) 
            {
                Console.WriteLine($"POKEMON: No types found, returning 1.0");
                return 1f; // no types = neutral
            }

            float multiplier = 1f;
            foreach (var defenderType in types)
            {
                var factor = GetSingleTypeFactor(attackAttr, defenderType, catalog);
                Console.WriteLine($"POKEMON: Type {defenderType} factor = {factor}");
                if (factor == 0f) 
                {
                    Console.WriteLine($"POKEMON: Immunity detected! Returning 0");
                    return 0f; // any immunity → total immunity
                }
                multiplier *= factor; // e.g., 0.5 × 0.5 = 0.25 for double resist
            }
            Console.WriteLine($"POKEMON: Final multiplier = {multiplier}");
            return multiplier;
        }

        // Instance method wrapper for non-static contexts
        private float GetPokemonTypeMultiplier(Guid attackAttr, BattleCharacterBase target)
        {
            return GetPokemonTypeMultiplier(attackAttr, target, catalog);
        }

        /// <summary>
        /// Pokemon-style weather effects that modify damage based on move type
        /// Reads from in-game variable "Weather" (0=None, 1=Rain, 2=Sun, 3=Sandstorm, 4=Hail)
        /// </summary>
        private static float GetWeatherMultiplier(BattleCharacterBase attacker, BattleCharacterBase target)
        {
            try
            {
                // Read weather from in-game variable "Weather"
                int weatherValue = (int)GameMain.instance.data.system.GetVariable("Weather");
                
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "WEATHER SYSTEM", 
                    string.Format("🌤️ Current weather value: {0}", weatherValue));

                // Get the attack types from the skill being used
                var attackTypes = ExtractTypesFromSkill(attacker);
                if (attackTypes.Count == 0)
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "WEATHER SYSTEM", 
                        "❌ No attack types found - weather has no effect");
                    return 1.0f; // No weather effect if no attack type
                }

                string primaryAttackType = attackTypes[0].ToLowerInvariant();
                float weatherMultiplier = 1.0f;

                // Apply Pokemon-style weather effects
                switch (weatherValue)
                {
                    case 1: // Rain
                        if (primaryAttackType == "water")
                            weatherMultiplier = 1.5f; // Rain boosts Water moves
                        else if (primaryAttackType == "fire")
                            weatherMultiplier = 0.5f; // Rain weakens Fire moves
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "WEATHER SYSTEM", 
                            string.Format("🌧️ RAIN: {0} type gets {1}x multiplier", primaryAttackType, weatherMultiplier));
                        break;

                    case 2: // Sun
                        if (primaryAttackType == "fire")
                            weatherMultiplier = 1.5f; // Sun boosts Fire moves
                        else if (primaryAttackType == "water")
                            weatherMultiplier = 0.5f; // Sun weakens Water moves
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "WEATHER SYSTEM", 
                            string.Format("☀️ SUN: {0} type gets {1}x multiplier", primaryAttackType, weatherMultiplier));
                        break;

                    case 3: // Sandstorm
                        if (primaryAttackType == "rock" || primaryAttackType == "ground" || primaryAttackType == "steel")
                            weatherMultiplier = 1.2f; // Sandstorm slightly boosts Rock/Ground/Steel
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "WEATHER SYSTEM", 
                            string.Format("🌪️ SANDSTORM: {0} type gets {1}x multiplier", primaryAttackType, weatherMultiplier));
                        break;

                    case 4: // Hail
                        if (primaryAttackType == "ice")
                            weatherMultiplier = 1.2f; // Hail slightly boosts Ice moves
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "WEATHER SYSTEM", 
                            string.Format("🧊 HAIL: {0} type gets {1}x multiplier", primaryAttackType, weatherMultiplier));
                        break;

                    case 0: // No weather
                    default:
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "WEATHER SYSTEM", 
                            "🌤️ CLEAR: No weather effects");
                        weatherMultiplier = 1.0f;
                        break;
                }

                return weatherMultiplier;
            }
            catch (System.Exception ex)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "WEATHER ERROR", 
                    string.Format("❌ Weather calculation failed: {0}", ex.Message));
                return 1.0f; // Default to no weather effect on error
            }
        }

        /// <summary>
        /// Simplified type effectiveness for formula context
        /// Uses character types and assumes current skill for attack type
        /// </summary>
        /// <param name="attacker">Attacking character</param>
        /// <param name="target">Target character</param>
        /// <returns>Type effectiveness multiplier</returns>
        private static float GetSimpleTypeEffectiveness(BattleCharacterBase attacker, BattleCharacterBase target)
        {
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "EDEF DEBUG", 
                string.Format("🔧 GetSimpleTypeEffectiveness called: {0} vs {1}", 
                attacker?.Name ?? "null", target?.Name ?? "null"));
            
            if (attacker == null || target == null) 
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "EDEF DEBUG", "Null attacker or target, returning 1.0f");
                return 1.0f;
            }
            
            // Get types from characters and skills
            var attackerTypes = ExtractTypeTagsFromCharacter(attacker);
            var targetTypes = ExtractTypeTagsFromCharacter(target);
            var attackTypes = ExtractTypesFromSkill(attacker);  // ✅ Get skill types
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "SIMPLE TYPE", 
                string.Format("Simple calc: Attacker {0} [{1}] using skill [{2}] vs Target {3} [{4}]", 
                attacker.Name, string.Join(",", attackerTypes), string.Join(",", attackTypes), target.Name, string.Join(",", targetTypes)));
            
            if (attackTypes.Count == 0 || targetTypes.Count == 0) 
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "SIMPLE TYPE", "No skill types or target types found, returning 1.0x");
                return 1.0f;
            }
            
            // Use the first SKILL type as the attack type (correct for Pokemon battles)
            string attackType = attackTypes[0];
            
            // Calculate effectiveness against all target types
            float totalEffectiveness = 1.0f;
            foreach (var defenseType in targetTypes)
            {
                float typeMultiplier = GetTypeEffectiveness(attackType, defenseType);
                totalEffectiveness *= typeMultiplier;
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "SIMPLE TYPE", 
                    string.Format("{0} vs {1} = {2}x", attackType, defenseType, typeMultiplier));
            }
            
            // Apply STAB if attacker type matches (simplified - just check first type)
            float stabBonus = attackerTypes.Contains(attackType) ? 1.5f : 1.0f;
            float finalMultiplier = totalEffectiveness * stabBonus;
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "SIMPLE TYPE", 
                string.Format("Final: {0}x × STAB {1}x = {2}x", totalEffectiveness, stabBonus, finalMultiplier));
            
            return finalMultiplier;
        }


        /// <summary>
        /// 2Dバトルの背景タイプ
        /// 2D battle background type
        /// </summary>
        enum BackGroundStyle
        {
            FillColor,
            Image,
            Model,
        }
        private bool _isDrawingBattleSceneFlg = false;

        public BattleResultState BattleResult { get; private set; }
        public override bool IsPlayingBattleEffect { get; set; }
        public override bool IsDrawingBattleScene
        {
            get { return _isDrawingBattleSceneFlg; }
            set
            {
                _isDrawingBattleSceneFlg = value;
                setActorsVisibility(value);
            }
        }

        public BattleViewer battleViewer;
        public ResultViewer resultViewer;
        TweenColor fadeScreenColorTweener;

        Dictionary<Guid, Common.Resource.Texture> iconTable;

        internal BattleState battleState;
        BattleState prevBattleState;
        const BattleState WAIT_CTB_GAUGE = BattleState.Free1;
        internal SelectBattleCommandState battleCommandState;
        bool escapeAvailable;
        bool gameoverOnLose;
        float battleStateFrameCount;


        GameMain owner;
        Catalog catalog;
        Rom.GameSettings gameSettings;

        // 戦闘に必要なステータス
        // battle stats
        Party party;
        public int battlePlayerMax;
        public int battleEnemyMax;
        List<BattleCommand> playerBattleCommand;
        internal List<BattlePlayerData> playerData;
        internal List<BattlePlayerData> stockPlayerData;
        internal List<BattleCharacterBase> targetPlayerData;
        internal List<BattleEnemyData> enemyData;
        internal List<BattleEnemyData> stockEnemyData;
        internal List<BattleCharacterBase> targetEnemyData;
        List<BattlePlayerData> playerViewData;
        public override List<BattlePlayerData> PlayerViewDataList => playerViewData;
        List<BattleEnemyData> enemyMonsterViewData;
        public override List<BattleEnemyData> EnemyViewDataList => enemyMonsterViewData;
        Dictionary<Guid, BattleViewer3D.BattleUI.LayoutDrawerController> layoutDic = new Dictionary<AttackAttributeType, BattleViewer3D.BattleUI.LayoutDrawerController>();

        BattlePlayerData leavePlayer;
        BattlePlayerData enterPlayer;
        BattleEnemyData leaveEnemy;
        BattleEnemyData enterEnemy;


        BattleEnemyInfo[] monsterLayouts;

        internal BattleCharacterBase activeCharacter;
        int attackCount;
        internal BattlePlayerData commandSelectPlayer;

        public class BattleActionEntry
        {
            public Action setter;
            public BattleCharacterBase character;

            public BattleActionEntry(BattleCharacterBase target)
            {
                character = target;
            }

            public BattleActionEntry(BattleCharacterBase target, Action p) : this(target)
            {
                setter = p;
            }
        }
        List<BattleActionEntry> battleEntryCharacters;
        int commandSelectedMemberCount;
        int commandExecuteMemberCount;
        List<RecoveryStatusInfo> recoveryStatusInfo;
        private BattleInfo battleInfo;

        // 状態異常
        // Abnormal status
        public List<Guid> displayedContinueConditions = new List<Guid>();
        public Dictionary<BattleCharacterBase, List<Rom.Condition>> displayedSetConditionsDic = new Dictionary<BattleCharacterBase, List<Rom.Condition>>();

        public BattleViewer3D Viewer { get { return (BattleViewer3D)battleViewer; } }

        /// <summary>
        /// 敵味方どちらが先制攻撃するか
        /// Which enemy or ally will attack first?
        /// </summary>
        enum FirstAttackType
        {
            None = 0,
            Player,
            Monster,
        }

        FirstAttackType firstAttackType = FirstAttackType.None;

        int playerEscapeFailedCount;
        
        // Speed-based turn system - characters act in order of their speed stat
        
        // Pokemon-style: Pre-battle showcase variables
        private bool isPreBattleShowcase = false;
        private float preBattleShowcaseTimer = 0f;
        private const float PRE_BATTLE_SHOWCASE_DURATION = 3.0f; // 5 seconds to show both teams
        
        // Capture mechanics
        private List<BattleEnemyData> capturedEnemies = new List<BattleEnemyData>();
        private const string CAPTURE_STATUS_NAME = "Capture"; // Name of the capture status condition
        private static int captureCounter = 0; // Static counter for unique captured enemy IDs

        // Evolution/Digimon system
        private bool showingShoices;
        private int currentIndexSelected;
        private bool selectingEvoDone;
        private string[] currentEvolutionList;
        private float prevDirection = -1;
        
        /// <summary>
        /// Check if an enemy has the Capture status condition
        /// </summary>
        /// <param name="enemy">Enemy to check</param>
        /// <returns>True if enemy has Capture status</returns>
        private bool HasCaptureStatus(BattleEnemyData enemy)
        {
            if (enemy == null || enemy.conditionInfoDic == null)
                return false;
                
            foreach (var conditionEntry in enemy.conditionInfoDic)
            {
                var condition = conditionEntry.Value.rom;
                if (condition != null && condition.name == CAPTURE_STATUS_NAME)
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                        string.Format("Enemy {0} has Capture status condition", enemy.Name));
                    return true;
                }
            }
            return false;
        }
        
        /// <summary>
        /// Get all enemies that have the Capture status and are eligible for capture
        /// </summary>
        /// <returns>List of capturable enemies</returns>
        private List<BattleEnemyData> GetCapturableEnemies()
        {
            var capturableEnemies = new List<BattleEnemyData>();
            
            // Check active enemies
            foreach (var enemy in enemyData)
            {
                if (HasCaptureStatus(enemy))
                {
                    capturableEnemies.Add(enemy);
                }
            }
            
            // Check stock enemies
            foreach (var enemy in stockEnemyData)
            {
                if (HasCaptureStatus(enemy))
                {
                    capturableEnemies.Add(enemy);
                }
            }
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                string.Format("Found {0} capturable enemies", capturableEnemies.Count));
                
            return capturableEnemies;
        }
        
                                  /// <summary>
        /// Process captured enemies - add them to the player's collection
        /// </summary>
                private void ProcessCapturedEnemies()
        {
            var capturableEnemies = GetCapturableEnemies();
            
            // Collect enemy switches to perform after capture processing
            var enemySwitchesAfterCapture = new List<(BattleEnemyData captured, BattleEnemyData incoming)>();
            
            foreach (var enemy in capturableEnemies)
            {
                // Only add if not already captured (prevent duplicates from multiple CheckBattleFinish calls)
                if (!capturedEnemies.Contains(enemy))
                {
                    capturedEnemies.Add(enemy);
                    
                    // Add captured enemy to party as a new party member
                    // Use the proper party system to add the captured monster
                 var totalPartySize = party.PlayersInMenu.Count + party.Reserves.Count;
                 if (totalPartySize < 999)
                 {
                     // Create a UNIQUE captured enemy instance (like Pokemon!)
                     var enemyLevel = Math.Max(1, enemy.monsterGameData?.level ?? 1);
                     
                     var levelText = enemy.monsterGameData != null ? enemy.monsterGameData.level.ToString() : "null";
                     GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                         string.Format("Capturing {0}: monsterGameData.level = {1}, using level = {2}", 
                         enemy.Name, levelText, enemyLevel));
                     
                     // Create hero from original ROM Cast as template
                     var capturedHero = Party.createHeroFromRom(catalog, party, enemy.monster, enemyLevel, true);
                     
                     if (capturedHero != null)
                     {
                         // Make this captured enemy UNIQUE by creating separate ROM identity
                         captureCounter++;
                         var captureId = captureCounter;
                         
                         // Create unique name for logging: "Species Name #ID" (e.g., "Skeleton Fighter #1")
                         var uniqueName = string.Format("{0} #{1}", enemy.monster.name, captureId);
                         
                         // Set current HP/MP to captured values (preserving battle state)
                         capturedHero.hitpoint = Math.Max(1, enemy.HitPoint);
                         capturedHero.magicpoint = enemy.MagicPoint;
                         
                         // PRESERVE BATTLE STATES: Copy all conditions from enemy to captured hero
                         // This ensures captured monsters retain their battle states (buffs, debuffs, etc.)
                         if (enemy.conditionInfoDic != null && enemy.conditionInfoDic.Count > 0)
                         {
                             // Clear the default conditions from createHeroFromRom
                             capturedHero.conditionInfoDic.Clear();
                             
                             // Copy all battle conditions from the enemy (except capture status)
                             foreach (var conditionEntry in enemy.conditionInfoDic)
                             {
                                 var condition = conditionEntry.Value.rom;
                                 if (condition != null && condition.name != CAPTURE_STATUS_NAME)
                                 {
                                     capturedHero.conditionInfoDic.Add(conditionEntry.Key, conditionEntry.Value);
                                     GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                                         string.Format("Preserved condition '{0}' on captured {1}", condition.name, uniqueName));
                                 }
                             }
                             
                             // Refresh condition effects to apply the preserved states
                             capturedHero.refreshConditionEffect();
                             
                             GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                                 string.Format("Preserved {0} battle states for captured {1}", 
                                 capturedHero.conditionInfoDic.Count, uniqueName));
                         }
                         
                         // Note: Hero names are managed by the Party system, not directly on Hero objects
                         // The unique identity is preserved through the capture counter and battle state
                         var nameWithSuffix = uniqueName + " (Captured)";
                         
                         // Actually add the hero to the party using proper methods
                         // Try the user's suggested method first
                         try
                         {
                             party.AddReserve(capturedHero);
                             GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                                 string.Format("Added {0} to reserves using AddReserve! HP: {1}", nameWithSuffix, capturedHero.hitpoint));
                         }
                         catch (Exception ex)
                         {
                             // Fallback: Try other methods if AddReserve doesn't exist
                             GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                                 string.Format("AddReserve failed: {0}, trying fallback...", ex.Message));
                             
                             // Try SetReserve method if it exists
                             try
                             {
                                 int reserveIndex = party.Reserves.Count;
                                 party.SetReserve(capturedHero, reserveIndex);
                                 GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                                     string.Format("Added {0} to reserves using SetReserve! HP: {1}", nameWithSuffix, capturedHero.hitpoint));
                             }
                             catch
                             {
                                 // Final fallback: direct list manipulation
                                 party.Reserves.Add(capturedHero);
                                 GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                                     string.Format("Added {0} to reserves using direct Add! HP: {1}", nameWithSuffix, capturedHero.hitpoint));
                             }
                         }
                         
                         // IV SYSTEM: Generate random Individual Values AFTER hero is added to party
                         // This ensures party management doesn't reset the stats we apply
                         GenerateIVsForCapturedHero(capturedHero, uniqueName);
                         
                         // NATURE SYSTEM: Assign random nature and apply stat modifications
                         GenerateNatureForCapturedHero(capturedHero, uniqueName);
                         
                         // SHINY SYSTEM: Random chance for shiny variant
                         GenerateShinyStatusForCapturedHero(capturedHero, uniqueName);
                     }
                 }
                 else
                 {
                     GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                         string.Format("Party is full! {0} was captured but not added to active party.", enemy.Name));
                 }
                 
                 // Check for auto-switch when enemy is captured if reserves available
                 if (stockEnemyData.Count > 0 && stockEnemyData.Any(e => e.HitPoint > 0))
                 {
                     var switchTarget = stockEnemyData.FirstOrDefault(e => e.HitPoint > 0);
                     if (switchTarget != null)
                     {
                         enemySwitchesAfterCapture.Add((enemy, switchTarget));
                         GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                             string.Format("Next enemy {0} will replace captured {1}", switchTarget.Name, enemy.Name));
                     }
                 }
                 
                 // Add visual fadeout effect for capture
                 battleViewer.AddFadeOutCharacter(enemy);
                 
                 // Play capture effect
                 battleViewer.SetDisplayMessage(string.Format("{0} was captured!", enemy.Name));
                 
                 GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                     string.Format("Processed capture of enemy {0}", enemy.Name));
                }
             }
             
             // Perform enemy switches after capture processing is complete
             foreach (var (captured, incoming) in enemySwitchesAfterCapture)
             {
                 PerformEnemyAutoSwitch(captured, incoming);
                 
                 GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                     string.Format("Enemy {0} automatically sent out to replace captured {1}", incoming.Name, captured.Name));
                     
                 // Show message about new enemy
                 battleViewer.SetDisplayMessage(string.Format("{0} was sent out!", incoming.Name));
             }
             
             // Remove captured enemies that don't have replacements from battle lists
             foreach (var enemy in capturableEnemies)
             {
                 // Only remove if this enemy wasn't replaced (not in the switch list)
                 if (!enemySwitchesAfterCapture.Any(pair => pair.captured == enemy))
                 {
                     // Remove from view data and battle lists
                     removeVisibleEnemy(enemy);
                     enemyData.Remove(enemy);
                     targetEnemyData.Remove(enemy);
                     stockEnemyData.Remove(enemy);
                     
                     GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                         string.Format("Removed captured enemy {0} from battle (no replacement)", enemy.Name));
                 }
             }
         }
         
         /// <summary>
         /// Check for immediate captures during battle turns
         /// This allows enemies to be captured as soon as they receive the Capture status
         /// </summary>
                 private void CheckForImmediateCaptures()
        {
            var capturableEnemies = GetCapturableEnemies();
            
            if (capturableEnemies.Count > 0)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                    string.Format("Found {0} enemies with Capture status during turn", capturableEnemies.Count));
                
                // Process captures immediately
                ProcessCapturedEnemies();
            }
        }
        
        private void UpdateBattleState_ResultInit()
        {
            if (!battleEvents.isBusy())
            {
                battleEvents.clearCurrentProcessingTrigger();
                ChangeBattleState(BattleState.Result);
            }
        }

        private void UpdateBattleState_Result()
        {
            resultProperty = resultViewer.Update();

            if (resultViewer.IsEnd && (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU) ||
                        Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.TOUCH, Input.GameState.SYSTEM) || resultViewer.clickedCloseButton))
            {
                resultViewer.clickedCloseButton = false;
                Audio.PlaySound(owner.se.decide);

                BattleResult = BattleResultState.Win;

                battleEvents.start(Rom.Script.Trigger.BATTLE_AFTER_RESULT);

                // バトル終了に遷移
                // Transition to battle end
                ChangeBattleState(BattleState.SetFinishEffect);
            }
        }

        private void UpdateBattleState_PlayerEscapeSuccess()
        {
            // 「バトルの強制終了」コマンドのタイミング次第で、battleViewer.IsEffectEndPlayがfalseのまま遷移して次のバトルが進まなくなるので、チェックを追加
            // Depending on the timing of the "Forced End of Battle" command, battleViewer.IsEffectEndPlay may remain false and the next battle will not proceed, so a check is added
            if (battleStateFrameCount >= 90 && !battleEvents.isBusy() && battleViewer.IsEffectEndPlay)
            {
                ApplyPlayerDataToGameData();

                battleViewer.CloseWindow();

                BattleResult = BattleResultState.Escape;
                battleEvents.setBattleResult(BattleResult);
                battleEvents.start(Rom.Script.Trigger.BATTLE_END);
                ChangeBattleState(BattleState.SetFinishEffect);
            }
        }

        private void UpdateBattleState_StopByEvent()
        {
            UpdateBattleState_PlayerEscapeSuccess();
        }

        private void UpdateBattleState_PlayerEscapeFail()
        {
            if (battleStateFrameCount >= 90)
            {
                activeCharacter.ExecuteCommandEnd();

                battleViewer.CloseWindow();

                battleEvents.start(Rom.Script.Trigger.BATTLE_AFTER_ACTION);

                ChangeBattleState(BattleState.CheckBattleCharacterDown1);
            }
        }

        private void UpdateBattleState_MonsterEscape()
        {
            if (battleStateFrameCount >= 60)
            {
                battleViewer.CloseWindow();

                var escapedMonster = (BattleEnemyData)activeCharacter;

                enemyData.Remove(escapedMonster);
                targetEnemyData.Remove(escapedMonster);

                battleEvents.start(Rom.Script.Trigger.BATTLE_AFTER_ACTION);

                ChangeBattleState(BattleState.BattleFinishCheck1);
            }
        }

        private void UpdateBattleState_SetFinishEffect()
        {
            if (battleEvents.isBusy())
                return;
            battleEvents.clearCurrentProcessingTrigger();

            // バトルイベントでHP/MPを改変している可能性があるため、念の為もう一度適用する。
            // It is possible that HP/MP has been modified in the battle event, so please apply it again just in case.
            ApplyPlayerDataToGameData();

            var guid = owner.data.system.transitionBattleLeave.HasValue ? owner.data.system.transitionBattleLeave.Value : gameSettings.transitionBattleLeave;
            if (catalog.getItemFromGuid(guid) == null)
                fadeScreenColorTweener.Begin(new Color(Color.Black, 0), new Color(Color.Black, 255), 30);
            else
                owner.mapScene.SetWipe(guid);

                        ChangeBattleState(BattleState.FinishFadeOut);
        }



        
        /// <summary>
        /// Pokemon-style: Get all targetable party members (active + reserves) for skills and items
        /// This allows you to use healing items (like Potions) and support skills (like healing magic) 
        /// on ANY party member, including those in reserves!
        /// </summary>
        /// <returns>List of all party members that can be targeted</returns>
        private List<BattlePlayerData> GetAllTargetablePartyMembers()
        {
            var allMembers = new List<BattlePlayerData>();
            
            // Add active party members
            allMembers.AddRange(targetPlayerData.Cast<BattlePlayerData>());
            
            // Add reserve party members and set up their display properties for targeting UI
            for (int i = 0; i < stockPlayerData.Count; i++)
            {
                var reserveMember = stockPlayerData[i];
                
                // Ensure reserve members have valid status window positions for targeting UI
                if (reserveMember.statusWindowDrawPosition == Vector2.Zero)
                {
                    // Create a temporary position for targeting UI (off-screen but valid)
                    // Position them in a column on the right side of the screen
                    reserveMember.statusWindowDrawPosition = new Vector2(
                        Graphics.ScreenWidth - 200, // Right side of screen
                        100 + (i * 80)              // Spaced vertically
                    );
                    
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Targeting", 
                        string.Format("Set reserve member {0} display position to ({1}, {2})", 
                        reserveMember.Name, reserveMember.statusWindowDrawPosition.X, reserveMember.statusWindowDrawPosition.Y));
                }
                
                // Ensure battle status data is set up for display
                if (reserveMember.battleStatusData == null)
                {
                    reserveMember.battleStatusData = new BattleStatusWindowDrawer.StatusData();
                    reserveMember.battleStatusData.Name = reserveMember.Name;
                    reserveMember.battleStatusData.HitPoint = reserveMember.HitPoint;
                    reserveMember.battleStatusData.MaxHitPoint = reserveMember.MaxHitPoint;
                    reserveMember.battleStatusData.MagicPoint = reserveMember.MagicPoint;
                    reserveMember.battleStatusData.MaxMagicPoint = reserveMember.MaxMagicPoint;
                }
                
                allMembers.Add(reserveMember);
            }
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Targeting", 
                string.Format("Pokemon-style targeting: {0} active + {1} reserve = {2} total targetable party members", 
                targetPlayerData.Count, stockPlayerData.Count, allMembers.Count));
            
            return allMembers;
        }

        /// <summary>
        /// Pokemon-style: Start pre-battle showcase showing both teams' full parties
        /// </summary>
        private void StartPreBattleShowcase()
        {
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                string.Format("Setting up pre-battle showcase - Players: {0} active + {1} reserves, Enemies: {2} active + {3} reserves", 
                playerData.Count, stockPlayerData.Count, enemyData.Count, stockEnemyData.Count));
            
            var viewer3D = battleViewer as BattleViewer3D;
            if (viewer3D == null)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", "Not 3D battle - cannot show full party showcase");
                return;
            }

            // CRITICAL: Temporarily make ALL characters "battle ready" for the showcase
            // This ensures 3D actors are generated for everyone
            foreach (var player in stockPlayerData)
            {
                player.IsBattle = true;
                player.IsStock = false;
            }
            foreach (var enemy in stockEnemyData)  
            {
                enemy.IsBattle = true;
                enemy.IsStock = false;
            }
            
            // Show all player party members (active + reserves) in a line formation
            ShowAllPartyMembersInShowcase(true); // true = player team
            
            // Show all enemy party members (active + reserves) in a line formation  
            ShowAllPartyMembersInShowcase(false); // false = enemy team
            
            // Set message for the showcase
            battleViewer.SetDisplayMessage("Team Lineups", WindowType.None);
        }
        
        /// <summary>
        /// Pokemon-style: Show all party members (active + reserves) in line formation for showcase
        /// </summary>
        private void ShowAllPartyMembersInShowcase(bool isPlayerTeam)
        {
            var viewer3D = battleViewer as BattleViewer3D;
            if (viewer3D == null) return;

            if (isPlayerTeam)
            {
                                 // Combine active players + reserves for showcase
                 var allPlayers = new List<BattlePlayerData>();
                 allPlayers.AddRange(playerData);
                 allPlayers.AddRange(stockPlayerData);
                 
                 float spacing = 2.0f; // Better horizontal spacing for showcase (more spread out)
                 Vector3 basePosition = BattleSequenceManagerBase.battleFieldCenter;
                 // Keep X centered, move player team DOWN (south)
                 basePosition.Z += 3.0f; // Move player team DOWN by 3 units
                
                                 GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                     string.Format("Showing {0} player team members in SOUTH formation", allPlayers.Count));
                
                                 for (int i = 0; i < allPlayers.Count; i++)
                 {
                     var player = allPlayers[i];
                     Vector3 showcasePosition = new Vector3(
                         basePosition.X + (i - (allPlayers.Count - 1) * 0.5f) * spacing, // Horizontal formation
                         basePosition.Y,
                         basePosition.Z
                     );
                    
                                         // Update player position and make visible
                     player.IsBattle = true;
                     player.SetPosition(showcasePosition);
                     player.directionRad = -(float)Math.PI / 2; // Face UP/north (toward enemies)
                    
                                         // Generate or update 3D actor for showcase
                     var existingActor = viewer3D.searchFromActors(player);
                     if (existingActor != null)
                     {
                         // Existing actor - just reposition
                         existingActor.mapChr.setPosition(showcasePosition);
                         existingActor.mapChr.setDirectionFromRadian(player.directionRad);
                         existingActor.mapChr.setVisibility(true);
                         
                         GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                             string.Format("Repositioned existing actor for {0}", player.Name));
                     }
                     else
                     {
                         // No existing actor - generate new one for showcase (including reserves)
                         GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                             string.Format("Generating new 3D actor for {0} (reserve member)", player.Name));
                         
                         BattleActor.party = owner.data.party;
                         var newActor = BattleActor.GenerateFriend(catalog, player.player, i, allPlayers.Count);
                         newActor.source = player;
                         newActor.mapChr.setPosition(showcasePosition);
                         newActor.mapChr.setDirectionFromRadian(player.directionRad);
                         newActor.mapChr.setVisibility(true);
                         
                         // Update player reference
                         player.mapChr = newActor.mapChr;
                         
                         // Add to viewer's friends list if not already there
                         viewer3D.friends.Add(newActor);
                         
                         // Also add to battle data lists temporarily for showcase
                         if (!playerData.Contains(player))
                         {
                             playerData.Add(player);
                         }
                         if (!targetPlayerData.Contains(player))
                         {
                             targetPlayerData.Add(player);
                         }
                         if (!playerViewData.Contains(player))
                         {
                             playerViewData.Add(player);
                         }
                         
                         GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                             string.Format("Successfully created 3D actor for {0}", player.Name));
                     }
                    
                                         GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                         string.Format("Player {0} positioned at ({1:F1}, {2:F1}, {3:F1}) for north vs south showcase", 
                         player.Name, showcasePosition.X, showcasePosition.Y, showcasePosition.Z));
                }
            }
            else
            {
                                 // Combine active enemies + reserves for showcase
                 var allEnemies = new List<BattleEnemyData>();
                 allEnemies.AddRange(enemyData);
                 allEnemies.AddRange(stockEnemyData);
                 
                 float spacing = 2.0f; // Better horizontal spacing for showcase (more spread out)
                 Vector3 basePosition = BattleSequenceManagerBase.battleFieldCenter;
                 // Keep X centered, move enemy team UP (north)  
                 basePosition.Z -= 3.0f; // Move enemy team UP by 3 units
                
                                 GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                     string.Format("Showing {0} enemy team members in NORTH formation", allEnemies.Count));
                
                                 for (int i = 0; i < allEnemies.Count; i++)
                 {
                     var enemy = allEnemies[i];
                     Vector3 showcasePosition = new Vector3(
                         basePosition.X + (i - (allEnemies.Count - 1) * 0.5f) * spacing, // Horizontal formation
                         basePosition.Y,
                         basePosition.Z
                     );
                    
                                         // Update enemy position and make visible
                     enemy.IsBattle = true;
                     enemy.SetPosition(showcasePosition);
                     enemy.directionRad = (float)Math.PI / 2; // Face DOWN/south (toward players)
                    
                                         // Generate or update 3D actor for showcase
                     var existingActor = viewer3D.searchFromActors(enemy);
                     if (existingActor != null)
                     {
                         // Existing actor - just reposition
                         existingActor.mapChr.setPosition(showcasePosition);
                         existingActor.mapChr.setDirectionFromRadian(enemy.directionRad);
                         existingActor.mapChr.setVisibility(true);
                         
                         GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                             string.Format("Repositioned existing actor for {0}", enemy.Name));
                     }
                     else
                     {
                         // No existing actor - generate new one for showcase (including reserves)
                         GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                             string.Format("Generating new 3D actor for {0} (reserve enemy)", enemy.Name));
                         
                         var newActor = BattleActor.GenerateEnemy(catalog, enemy, i, allEnemies.Count);
                         newActor.source = enemy;
                         newActor.mapChr.setPosition(showcasePosition);
                         newActor.mapChr.setDirectionFromRadian(enemy.directionRad);
                         newActor.mapChr.setVisibility(true);
                         
                         // Update enemy reference
                         enemy.mapChr = newActor.mapChr;
                         
                         // Add to viewer's enemies list if not already there
                         viewer3D.enemies.Add(newActor);
                         
                         // Also add to battle data lists temporarily for showcase
                         if (!enemyData.Contains(enemy))
                         {
                             enemyData.Add(enemy);
                         }
                         if (!targetEnemyData.Contains(enemy))
                         {
                             targetEnemyData.Add(enemy);
                         }
                         if (!enemyMonsterViewData.Contains(enemy))
                         {
                             enemyMonsterViewData.Add(enemy);
                         }
                         
                         GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                             string.Format("Successfully created 3D actor for {0}", enemy.Name));
                     }
                    
                                         GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                         string.Format("Enemy {0} positioned at ({1:F1}, {2:F1}, {3:F1}) for north vs south showcase", 
                         enemy.Name, showcasePosition.X, showcasePosition.Y, showcasePosition.Z));
                }
            }
        }
        
        /// <summary>
        /// Pokemon-style: End the pre-battle showcase and set up 1v1 Pokemon-style battle
        /// </summary>
        private void EndPreBattleShowcaseAndStartPokemonBattle()
        {
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                string.Format("Ending showcase and setting up Pokemon-style 1v1 battle. Current counts - Players: {0}, Enemies: {1}", 
                playerData.Count, enemyData.Count));
            
            var viewer3D = battleViewer as BattleViewer3D;
            if (viewer3D == null) return;

            // Store the first active player and enemy for the 1v1 battle
            var primaryPlayer = playerData.Count > 0 ? playerData[0] : null;
            var primaryEnemy = enemyData.Count > 0 ? enemyData[0] : null;
            
            // Clear all battle data lists and rebuild them properly for Pokemon-style 1v1
            var allPlayers = new List<BattlePlayerData>(playerData);
            allPlayers.AddRange(stockPlayerData.Where(p => !allPlayers.Contains(p)));
            
            var allEnemies = new List<BattleEnemyData>(enemyData);
            allEnemies.AddRange(stockEnemyData.Where(e => !allEnemies.Contains(e)));

            // Clear and reset all lists
            playerData.Clear();
            targetPlayerData.Clear();
            playerViewData.Clear();
            stockPlayerData.Clear();
            
            enemyData.Clear();
            targetEnemyData.Clear();
            enemyMonsterViewData.Clear();
            stockEnemyData.Clear();

            // Set up Pokemon-style 1v1: only the first player and enemy are active
            if (primaryPlayer != null)
            {
                primaryPlayer.IsBattle = true;
                primaryPlayer.IsStock = false;
                playerData.Add(primaryPlayer);
                targetPlayerData.Add(primaryPlayer);
                playerViewData.Add(primaryPlayer);
                
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                    string.Format("Set {0} as active player for 1v1", primaryPlayer.Name));
            }
            
            if (primaryEnemy != null)
            {
                primaryEnemy.IsBattle = true;
                primaryEnemy.IsStock = false;
                enemyData.Add(primaryEnemy);
                targetEnemyData.Add(primaryEnemy);
                enemyMonsterViewData.Add(primaryEnemy);
                
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                    string.Format("Set {0} as active enemy for 1v1", primaryEnemy.Name));
            }

            // All other players and enemies become reserves
            foreach (var player in allPlayers)
            {
                if (player != primaryPlayer)
                {
                    player.IsBattle = false;
                    player.IsStock = true;
                    stockPlayerData.Add(player);
                    
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                        string.Format("Moved {0} to player reserves", player.Name));
                }
            }
            
            foreach (var enemy in allEnemies)
            {
                if (enemy != primaryEnemy)
                {
                    enemy.IsBattle = false;
                    enemy.IsStock = true;
                    stockEnemyData.Add(enemy);
                    
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                        string.Format("Moved {0} to enemy reserves", enemy.Name));
                }
            }
            
            // Clean up the 3D viewer lists to only contain active characters
            // Remove excess actors from friends and enemies lists
            while (viewer3D.friends.Count > playerData.Count)
            {
                var excessActor = viewer3D.friends[viewer3D.friends.Count - 1];
                excessActor?.Release();
                viewer3D.friends.RemoveAt(viewer3D.friends.Count - 1);
            }
            
            while (viewer3D.enemies.Count > enemyData.Count)
            {
                var excessActor = viewer3D.enemies[viewer3D.enemies.Count - 1];
                excessActor?.Release();
                viewer3D.enemies.RemoveAt(viewer3D.enemies.Count - 1);
            }

            // Reposition the remaining active characters to standard battle positions
            if (primaryPlayer != null)
            {
                Vector3 playerPosition = BattleCharacterPosition.getPosition(
                    BattleSequenceManagerBase.battleFieldCenter, 
                    BattleCharacterPosition.PosType.FRIEND, 0, 1);
                primaryPlayer.SetPosition(playerPosition);
                primaryPlayer.directionRad = -(float)Math.PI / 2; // Face UP/north (toward enemies)
                
                if (primaryPlayer.mapChr != null)
                {
                    primaryPlayer.mapChr.setPosition(playerPosition);
                    primaryPlayer.mapChr.setDirectionFromRadian(primaryPlayer.directionRad);
                    primaryPlayer.mapChr.setVisibility(true);
                    
                    // Force direction update to ensure player faces right
                    primaryPlayer.mapChr.setDirection(0, true); // 0 = face right/east
                    
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                        string.Format("Player {0} direction set to {1} radians (facing right)", 
                        primaryPlayer.Name, primaryPlayer.directionRad));
                }
                
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                    string.Format("Positioned active player {0} for 1v1 battle", primaryPlayer.Name));
            }
            
            if (primaryEnemy != null)
            {
                Vector3 enemyPosition = BattleCharacterPosition.getPosition(
                    BattleSequenceManagerBase.battleFieldCenter, 
                    BattleCharacterPosition.PosType.ENEMY, 0, 1);
                primaryEnemy.SetPosition(enemyPosition);
                primaryEnemy.directionRad = (float)Math.PI; // Face left (toward player)
                
                if (primaryEnemy.mapChr != null)
                {
                    primaryEnemy.mapChr.setPosition(enemyPosition);
                    primaryEnemy.mapChr.setDirectionFromRadian(primaryEnemy.directionRad);
                    primaryEnemy.mapChr.setVisibility(true);
                    
                    // Force direction update to ensure enemy faces left
                    primaryEnemy.mapChr.setDirection(1, true); // 1 = face left/west
                    
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                        string.Format("Enemy {0} direction set to {1} radians (facing left)", 
                        primaryEnemy.Name, primaryEnemy.directionRad));
                }
                
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                    string.Format("Positioned active enemy {0} for 1v1 battle", primaryEnemy.Name));
            }
            
            // Hide reserve members from the field (they'll be brought back during targeting/switching/victory)
            foreach (var reservePlayer in stockPlayerData)
            {
                if (reservePlayer.mapChr != null)
                {
                    reservePlayer.mapChr.setVisibility(false);
                }
            }
            
            foreach (var reserveEnemy in stockEnemyData)
            {
                if (reserveEnemy.mapChr != null)
                {
                    reserveEnemy.mapChr.setVisibility(false);
                }
            }
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                string.Format("Pokemon-style setup complete: {0} vs {1} with {2} + {3} in reserves", 
                playerData.Count > 0 ? playerData[0].Name : "None",
                enemyData.Count > 0 ? enemyData[0].Name : "None", 
                stockPlayerData.Count, stockEnemyData.Count));
        }

        /// <summary>
        /// Pokemon-style: Bring all party members (including reserves) onto the battlefield for victory celebration
        /// </summary>
        private void BringAllPartyMembersToField()
        {
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Victory", 
                string.Format("BringAllPartyMembersToField called! Active: {0}, Reserve: {1}", 
                playerData.Count, stockPlayerData.Count));
            
            if (stockPlayerData.Count == 0)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Victory", "No reserve party members to bring to field - victory celebration skipped");
                return;
            }

            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Victory", 
                string.Format("Bringing {0} reserve party members to battlefield for victory celebration", stockPlayerData.Count));

            var viewer3D = battleViewer as BattleViewer3D;
            if (viewer3D == null)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Victory", "Not 3D battle - cannot bring party to field");
                return;
            }

            // Calculate positions for all party members in a line formation
            var activePlayer = playerData.FirstOrDefault();
            if (activePlayer == null) return;

            Vector3 basePosition = activePlayer.pos;
            float spacing = 1.0f; // Close formation for victory celebration
            
            int totalMembers = playerData.Count + stockPlayerData.Count;
            float startOffset = -(totalMembers - 1) * spacing * 0.5f; // Center the line

            // Reposition the existing active player (Ken) to the leftmost position
            Vector3 kenPosition = new Vector3(basePosition.X + startOffset, basePosition.Y, basePosition.Z);
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Victory", 
                string.Format("Victory formation: {0} total members, spacing {1}, Ken at position 0", totalMembers, spacing));
            activePlayer.SetPosition(kenPosition);
            var existingActor = viewer3D.searchFromActors(activePlayer);
            if (existingActor != null)
            {
                existingActor.mapChr.setPosition(kenPosition);
                existingActor.mapChr.setVisibility(true); // Ensure Ken is visible
                existingActor.queueActorState(BattleActor.ActorStateType.WIN); // Victory animation
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Victory", 
                    string.Format("Repositioned {0} (active player) to position ({1:F1}, {2:F1}, {3:F1}) with WIN animation", 
                    activePlayer.Name, kenPosition.X, kenPosition.Y, kenPosition.Z));
            }
            else
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Victory", 
                    string.Format("ERROR: Could not find 3D actor for active player {0}!", activePlayer.Name));
                
                // Try to regenerate Ken's actor if not found
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Victory", "Attempting to regenerate Ken's 3D actor...");
                BattleActor.party = owner.data.party;
                var newKenActor = BattleActor.GenerateFriend(catalog, activePlayer.player, 0, totalMembers);
                newKenActor.source = activePlayer;
                newKenActor.mapChr.setPosition(kenPosition);
                newKenActor.mapChr.setDirectionFromRadian(activePlayer.directionRad);
                newKenActor.mapChr.setVisibility(true);
                activePlayer.mapChr = newKenActor.mapChr;
                
                // Replace or add Ken's actor in the friends list
                if (viewer3D.friends.Count > 0)
                {
                    viewer3D.friends[0] = newKenActor; // Replace first slot with Ken
                }
                else
                {
                    viewer3D.friends.Add(newKenActor); // Add Ken if friends list is empty
                }
                
                newKenActor.queueActorState(BattleActor.ActorStateType.WIN); // Victory animation
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Victory", "Successfully regenerated Ken's 3D actor");
            }

            // Create a copy to avoid modifying collection during iteration
            var reservePlayersCopy = new List<BattlePlayerData>(stockPlayerData);
            
            // Bring out reserve party members one by one
            for (int i = 0; i < reservePlayersCopy.Count; i++)
            {
                var reservePlayer = reservePlayersCopy[i];
                int positionIndex = playerData.Count + i; // Position after existing active players
                
                // Calculate position for this party member
                Vector3 position = new Vector3(
                    basePosition.X + startOffset + (positionIndex * spacing),
                    basePosition.Y,
                    basePosition.Z
                );

                // Update player data
                reservePlayer.IsBattle = true;
                reservePlayer.IsStock = false;
                reservePlayer.SetPosition(position);
                reservePlayer.directionRad = -(float)Math.PI / 2; // Face UP/north (toward enemies)

                // Generate 3D actor for the reserve member
                BattleActor.party = owner.data.party;
                var newActor = BattleActor.GenerateFriend(catalog, reservePlayer.player, positionIndex, totalMembers);
                newActor.source = reservePlayer;
                newActor.mapChr.setPosition(position);
                newActor.mapChr.setDirectionFromRadian(reservePlayer.directionRad);
                reservePlayer.mapChr = newActor.mapChr;

                // Add to viewer's friends list
                viewer3D.friends.Add(newActor);

                // Add to active party data
                playerData.Add(reservePlayer);
                targetPlayerData.Add(reservePlayer);
                playerViewData.Add(reservePlayer);

                // Play appearance animation followed by victory celebration
                newActor.queueActorState(BattleActor.ActorStateType.APPEAR, "walk", 30);
                newActor.queueActorState(BattleActor.ActorStateType.APPEAR_END);
                newActor.queueActorState(BattleActor.ActorStateType.WIN); // Victory animation

                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Victory", 
                    string.Format("Brought {0} to battlefield at position ({1:F1}, {2:F1}, {3:F1}) with WIN animation", 
                    reservePlayer.Name, position.X, position.Y, position.Z));
            }

            // Clear the stock (reserves) since everyone is now on the field
            stockPlayerData.Clear();

            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Victory", 
                string.Format("Victory celebration: {0} party members now on battlefield!", totalMembers));
        }

        /// <summary>
        /// Pokemon-style: Determine if enemy should switch Pokemon
        /// </summary>
        /// <param name="enemyData">The current enemy</param>
        /// <returns>True if enemy should switch</returns>
        private bool ShouldEnemySwitch(BattleEnemyData enemyData)
        {
            // Basic switching logic: Switch if health is low (below 25%)
            if (enemyData.HitPointPercent < 0.25f)
            {
                // 75% chance to switch when low on health
                return battleRandom.Next(100) < 75;
            }
            
            // Small chance to switch even when healthy (10% chance)
            return battleRandom.Next(100) < 10;
        }
        
        /// <summary>
        /// Pokemon-style: Automatically switch enemies when one dies
        /// </summary>
        /// <param name="outgoingEnemy">The enemy that died</param>
        /// <param name="incomingEnemy">The reserve enemy to switch in</param>
        private void PerformEnemyAutoSwitch(BattleEnemyData outgoingEnemy, BattleEnemyData incomingEnemy)
        {
            // Find the position of the outgoing enemy
            var activeIdx = enemyData.IndexOf(outgoingEnemy);
            if (activeIdx < 0) return; // Safety check
            
            // SWITCH CLEAR SYSTEM: Clear #switchclear status ailments from outgoing enemy
            ClearSwitchClearableStatuses(outgoingEnemy);
            
            // Set up the incoming enemy for battle
            incomingEnemy.IsBattle = true;
            incomingEnemy.IsStock = false;
            incomingEnemy.SetPosition(outgoingEnemy.pos);
            incomingEnemy.directionRad = outgoingEnemy.directionRad;
            
            // Set up the outgoing enemy as reserve (even though dead)
            outgoingEnemy.IsBattle = false;
            outgoingEnemy.IsStock = true;
            
            // Update all relevant lists
            enemyData[activeIdx] = incomingEnemy;
            targetEnemyData[activeIdx] = incomingEnemy;
            enemyMonsterViewData[activeIdx] = incomingEnemy;
            
            // Move outgoing enemy to stock and remove incoming from stock
            stockEnemyData.Add(outgoingEnemy);
            stockEnemyData.Remove(incomingEnemy);
            
            // RETARGETING SYSTEM: Update all pending attacks to target the incoming enemy
            RetargetAllAttacksFromTo(outgoingEnemy, incomingEnemy);
            
            // Update 3D viewer if available
            if (battleViewer is BattleViewer3D viewer3D)
            {
                // Handle the visual switching for enemies - update existing actor to point to new character
                var actor = viewer3D.searchFromActors(outgoingEnemy);
                if (actor != null)
                {
                    actor.source = incomingEnemy;
                    incomingEnemy.mapChr = actor.mapChr;
                    incomingEnemy.actionHandler = outgoingEnemy.actionHandler;
                    
                    // Reset the actor state to alive
                    actor.queueActorState(BattleActor.ActorStateType.APPEAR, "walk", 20);
                    actor.queueActorState(BattleActor.ActorStateType.APPEAR_END);
                                 }
             }
         }
         
         /// <summary>
         /// Pokemon-style: Automatically switch players when one dies
         /// </summary>
         /// <param name="outgoingPlayer">The player that died</param>
         /// <param name="incomingPlayer">The reserve player to switch in</param>
         private void PerformPlayerAutoSwitch(BattlePlayerData outgoingPlayer, BattlePlayerData incomingPlayer)
         {
             // Find the position of the outgoing player
             var activeIdx = playerData.IndexOf(outgoingPlayer);
             if (activeIdx < 0) return; // Safety check
            
            // SWITCH CLEAR SYSTEM: Clear #switchclear status ailments from outgoing player
            ClearSwitchClearableStatuses(outgoingPlayer);
             
             // Set up the incoming player for battle
             incomingPlayer.IsBattle = true;
             incomingPlayer.IsStock = false;
             incomingPlayer.SetPosition(outgoingPlayer.pos);
             incomingPlayer.directionRad = -(float)Math.PI / 2; // Face UP/north (toward enemies)
             
             // Set up the outgoing player as reserve (even though dead)
             outgoingPlayer.IsBattle = false;
             outgoingPlayer.IsStock = true;
             
             // Update all relevant lists
             playerData[activeIdx] = incomingPlayer;
             targetPlayerData[activeIdx] = incomingPlayer;
             playerViewData[activeIdx] = incomingPlayer;
             
             // Move outgoing player to stock and remove incoming from stock
             stockPlayerData.Add(outgoingPlayer);
             stockPlayerData.Remove(incomingPlayer);
             
             // RETARGETING SYSTEM: Update all pending attacks to target the incoming player
             RetargetAllAttacksFromTo(outgoingPlayer, incomingPlayer);
             
             // Update 3D viewer if available - regenerate the actor for proper visual switching
             if (battleViewer is BattleViewer3D viewer3D)
             {
                 var oldActor = viewer3D.searchFromActors(outgoingPlayer);
                 if (oldActor != null)
                 {
                     GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "AutoSwitch", 
                         "Regenerating 3D actor for automatic switching");
                     
                     // Store the position and other important data
                     var position = oldActor.mapChr.getPosition();
                     var direction = outgoingPlayer.directionRad;
                     var playerIndex = activeIdx;
                     
                     // Release the old actor
                     oldActor.Release();
                     
                     // Create new actor for the incoming character
                     BattleActor.party = owner.data.party;
                     var newActor = BattleActor.GenerateFriend(catalog, incomingPlayer.player, playerIndex, playerData.Count);
                     newActor.source = incomingPlayer;
                     
                     // Set position and direction
                     newActor.mapChr.setPosition(position);
                     newActor.mapChr.setDirectionFromRadian(direction);
                     
                     // Update references
                     incomingPlayer.mapChr = newActor.mapChr;
                     incomingPlayer.actionHandler = outgoingPlayer.actionHandler;
                     
                     // Replace in the friends list
                     viewer3D.friends[playerIndex] = newActor;
                     
                     // Trigger entrance animation
                     newActor.queueActorState(BattleActor.ActorStateType.APPEAR, "walk", 20);
                     newActor.queueActorState(BattleActor.ActorStateType.APPEAR_END);
                     
                     GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "AutoSwitch", 
                         string.Format("3D model updated: {0} replaced with {1}", outgoingPlayer.Name, incomingPlayer.Name));
                 }
                 else
                 {
                     GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "AutoSwitch", 
                         "ERROR: Could not find 3D actor for automatic switching");
                 }
             }
             else
             {
                 GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "AutoSwitch", 
                     "No 3D viewer available for visual switching");
             }
         }
         
        bool IsFirstCommandSelectPlayer
        {
            get
            {
                var idx = playerData.IndexOf(playerData.Find(player => player.IsAnyCommandSelectable));

                if (idx < 0)
                {
                    return !IsDisabledEscape && (commandSelectedMemberCount == 0);
                }
                else
                {
                    return commandSelectedMemberCount == idx;
                }
            }
        }

        bool IsDisabledEscape => playerData.Any(player => player.IsDisabledEscape);

        BackGroundStyle backGroundStyle;
        Color backGroundColor;
        Common.Resource.Texture backGroundImageId = null;//#23959-1
                                                         //ModifiedModelInstance backGroundModel;

        internal TweenFloat statusUpdateTweener;
        TweenListManager<float, TweenFloat> openingBackgroundImageScaleTweener;

        Random battleRandom;

        internal BattleEventController battleEvents;
        public override BattleEventControllerBase BattleEvents { get => battleEvents; }
        int idNumber = Common.Rom.Map.maxMaxMonsters + 1;  // モンスターとかぶらないようにする / Avoid running into monsters


        private ResultViewer.ResultProperty resultProperty;

        private const float TIME_FOR_BATTLE_START_MESSEGE = 2f;
        private float elapsedTimeForBattleStart;
        private string battleStartWord;
        private int totalTurn;
        private int itemRate;
        private int moneyRate;
        private Guid waitForCommon;
        private Guid[] waitForCommons = new Guid[2];
        private ReflectionInfo[] reflections;
        internal static readonly Guid GUID_USE_ATTACKEFFEECT = new Guid("EDA33747-B8AD-4304-9FF0-52542D6F1A8B");

        public BattleSequenceManager(GameMain owner, Catalog catalog)
        {
            this.owner = owner;
            this.catalog = catalog;

            gameSettings = catalog.getGameSettings();

            BattleResult = BattleResultState.Standby;

            playerBattleCommand = new List<BattleCommand>();

            battleEntryCharacters = new List<BattleActionEntry>();

            battleRandom = new Random();

            if (owner.IsBattle2D)
            {
                battleViewer = new BattleViewer(owner);
            }
            else
            {
                battleViewer = new BattleViewer3D(owner);
                ((BattleViewer3D)battleViewer).setOwner(this);
            }

            resultViewer = new ResultViewer(owner);

            iconTable = new Dictionary<Guid, Common.Resource.Texture>();

            statusUpdateTweener = new TweenFloat();
            fadeScreenColorTweener = new TweenColor();
            openingBackgroundImageScaleTweener = new TweenListManager<float, TweenFloat>();

            rewards.LearnSkillResults = new List<Hero.LearnSkillResult>();
        }

        public override void Release()
        {
            if (!owner.IsBattle2D)
            {
                var viewer = battleViewer as BattleViewer3D;
                viewer.reset();
                viewer.finalize();
            }

			foreach (var item in layoutDic)
			{
                item.Value.finalize();
			}

            layoutDic.Clear();
        }

        public override void BattleStart(Party party, BattleEnemyInfo[] monsters, Common.Rom.Map.BattleSetting settings, bool escapeAvailable = true,
            bool gameoverOnLose = true, bool showMessage = true)
        {
            BattleStart(party, monsters, settings.layout?.PlayerLayouts, settings, escapeAvailable, gameoverOnLose, showMessage);

            if (owner != null)
            {
                foreach (var monster in monsters)
                {
                    owner.data.party.AddCast(monster.Id);
                }
            }
        }


        public override void BattleStart(Party party, BattleEnemyInfo[] monsters,
            Vector3[] playerLayouts, Common.Rom.Map.BattleSetting settings, bool escapeAvailable = true, bool gameoverOnLose = true, bool showMessage = true)
        {
            // エンカウントバトルの歩数リセット
            // Encounter battle step count reset
            owner.mapScene.mapEngine.genEncountStep();

            // null例外が出ないよう0個で初期化しておく
            // Initialize with 0 to prevent null exception
            playerData = new List<BattlePlayerData>();
            enemyData = new List<BattleEnemyData>();
            playerViewData = new List<BattlePlayerData>();
            enemyMonsterViewData = new List<BattleEnemyData>();

            // Initialize capture mechanics for new battle
            capturedEnemies.Clear();
            
            // Initialize evolution system for new battle (prevent state carryover)
            showingShoices = false;
            selectingEvoDone = false;
            currentIndexSelected = -1;
            currentEvolutionList = null;
            prevDirection = -1;

            this.escapeAvailable = escapeAvailable;
            this.gameoverOnLose = gameoverOnLose;
            this.party = party;

            // フラッシュ開始
            // flash start
            ChangeBattleState(BattleState.StartFlash);

            battleCommandState = SelectBattleCommandState.None;
            BattleResult = BattleResultState.NonFinish;
            IsPlayingBattleEffect = true;
            IsDrawingBattleScene = false;
            battleEntryCharacters.Clear();

            recoveryStatusInfo = new List<RecoveryStatusInfo>();

            battleStartWord = "";
            if (showMessage)
                battleStartWord = monsters.Length <= 1 ? gameSettings.glossary.battle_start_single : gameSettings.glossary.battle_start;

            battleInfo = new BattleInfo() { monsters = monsters, playerLayouts = playerLayouts, settings = settings };
        }

        class BattleInfo
        {
            public BattleEnemyInfo[] monsters;
            public Vector3[] playerLayouts;
            public Rom.Map.BattleSetting settings;
        }

        private void LoadBattleSceneImpl()
        {
            var monsters = battleInfo.monsters;
            var playerLayouts = battleInfo.playerLayouts;
            var settings = battleInfo.settings;

            // このタイミングで同期処理で読み込めるのはWINDOWSだけ(Unityではフリーズする)
            // Only WINDOWS can be read by synchronous processing at this timing (freezes in Unity)
#if WINDOWS
            if (battleViewer is BattleViewer3D)
                ((BattleViewer3D)battleViewer).catalog = owner.catalog;
            battleViewer.SetBackGround(owner.mapScene.map.getBattleBg(catalog, settings));
#endif

            // バトルマップの中心点が初期化されてないと敵の配置がおかしくなるので、敵の初期化の前に設定しておく
            // If the center point of the battle map is not initialized, the placement of the enemy will be strange, so set it before initializing the enemy.
            if (battleViewer is BattleViewer3D)
            {
                // ほかに battleViewer の初期化が必要な時はここに追加する
                // Add any other initialization of battleViewer here
                ((BattleViewer3D)battleViewer).SetBackGroundCenter(settings.battleBgCenterX, settings.battleBgCenterZ);
            }

            playerViewData = new List<BattlePlayerData>();
            enemyMonsterViewData = new List<BattleEnemyData>();
            targetPlayerData = new List<BattleCharacterBase>();
            targetEnemyData = new List<BattleCharacterBase>();


            battlePlayerMax = gameSettings.BattlePlayerMax;
            battleEnemyMax = monsters.Length;


            // プレイヤーの設定
            // player settings
            playerData = new List<BattlePlayerData>();
            stockPlayerData = new List<BattlePlayerData>();

            for (int index = 0; index < party.PlayerCount; index++)
            {
                addPlayerData(party.GetPlayer(index)).directionRad = -(float)Math.PI / 2; // Face UP/north (toward enemies)
            }

            enemyData = new List<BattleEnemyData>();
            stockEnemyData = new List<BattleEnemyData>();

            monsterLayouts = new BattleEnemyInfo[monsters.Length];

            // 敵の設定
            // enemy settings
            for (int index = 0; index < monsters.Length; index++)
            {
                if (!monsters[index].IsLayoutValid())
                    addEnemyData(monsters[index].Id, null, -1, monsters[index].Level);
                else
                    addEnemyData(monsters[index].Id, monsters[index].Layout, -1, monsters[index].Level);

                monsterLayouts[index] = monsters[index];
            }

            // Pokemon-style 1v1 battles: Only put 1 player and 1 enemy into active battle
            battlePlayerMax = Math.Min(1, stockPlayerData.Count); // Force 1v1 for Pokemon-style

            for (int i = 0; i < battlePlayerMax; i++)
            {
                // CRITICAL FIX: Select first ALIVE party member, not just first member
                // This prevents invisible party members when party leader is dead
                var player = stockPlayerData.FirstOrDefault(p => p.HitPoint > 0);
                if (player == null)
                {
                    // If no alive players, take the first one anyway (should trigger game over logic)
                    player = stockPlayerData[0];
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Battle Init", 
                        "Warning: No alive party members found, using dead party leader");
                }
                else
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Battle Init", 
                        string.Format("Selected {0} as active party member (HP: {1})", player.Name, player.HitPoint));
                }

                player.IsBattle = true;
                player.IsStock = false;

                if (playerLayouts == null || playerLayouts.Length <= i)
                {
                    player.SetPosition(BattleCharacterPosition.getPosition(BattleSequenceManagerBase.battleFieldCenter, BattleCharacterPosition.PosType.FRIEND, i, battlePlayerMax));
                }
                else
                {
                    player.SetPosition(BattleSequenceManagerBase.battleFieldCenter + playerLayouts[i]);
                }

                player.calcHeroLayout(playerData.Count);

                playerData.Add(player);
                stockPlayerData.Remove(player);
                targetPlayerData.Add(player);
            }

            battleEnemyMax = Math.Min(1, stockEnemyData.Count); // Force 1v1 for Pokemon-style

            for (int i = 0; i < battleEnemyMax; i++)
            {
                // Select first ALIVE enemy member, not just first member (consistency with player fix)
                var enemy = stockEnemyData.FirstOrDefault(e => e.HitPoint > 0);
                if (enemy == null)
                {
                    // If no alive enemies, take the first one anyway
                    enemy = stockEnemyData[0];
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Battle Init", 
                        "Warning: No alive enemies found, using dead enemy");
                }
                else
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Battle Init", 
                        string.Format("Selected {0} as active enemy (HP: {1})", enemy.Name, enemy.HitPoint));
                }

                enemy.IsBattle = true;
                enemy.IsStock = false;

                if (!enemy.IsManualPosition)
                {
                    enemy.SetPosition(BattleCharacterPosition.getPosition(BattleSequenceManagerBase.battleFieldCenter, BattleCharacterPosition.PosType.ENEMY, i, battleEnemyMax));
                }

                enemyData.Add(enemy);
                stockEnemyData.Remove(enemy);
                targetEnemyData.Add(enemy);
            }

            playerViewData.AddRange(playerData);
            enemyMonsterViewData.AddRange(enemyData);

            var playerAllData = new List<BattlePlayerData>();
            var enemyMonsterAllData = new List<BattleEnemyData>();

            playerAllData.AddRange(playerViewData);
            playerAllData.AddRange(stockPlayerData);
            enemyMonsterAllData.AddRange(enemyMonsterViewData);
            enemyMonsterAllData.AddRange(stockEnemyData);


            // アイコン画像の読み込み
            // Loading icon images
            var iconGuidSet = new HashSet<Guid>();

            foreach (var player in party.Players)
            {
                foreach (var commandGuid in player.rom.battleCommandList)
                {
                    var command = catalog.getItemFromGuid(commandGuid) as BattleCommand;

                    if (command != null) iconGuidSet.Add(command.icon.guId);
                }
            }

            foreach (var player in playerData)
            {
                foreach (var skill in player.player.skills)
                {
                    iconGuidSet.Add(skill.icon.guId);
                }
            }

            foreach (var item in party.Items)
            {
                iconGuidSet.Add(item.item.icon.guId);
            }

            iconGuidSet.Add(gameSettings.escapeIcon.guId);
            iconGuidSet.Add(gameSettings.returnIcon.guId);

            iconTable.Clear();

            foreach (var guid in iconGuidSet)
            {
                var icon = catalog.getItemFromGuid(guid) as Common.Resource.Texture;

                if (icon != null)
                {
                    Graphics.LoadImage(icon);

                    iconTable.Add(guid, icon);
                }
                else
                {
                    iconTable.Add(guid, null);
                }
            }

            commandSelectedMemberCount = 0;

            playerEscapeFailedCount = 0;
            
            // Pokemon-style: Reset pre-battle showcase flags for new battle
            isPreBattleShowcase = false;
            preBattleShowcaseTimer = 0f;

            battleViewer.BattleStart(playerAllData, enemyMonsterAllData);

            battleViewer.LoadResourceData(catalog, gameSettings);

            resultViewer.LoadResourceData(catalog);

            CheckBattleCharacterDown();

            var bg = catalog.getItemFromGuid(settings.battleBg) as Common.Resource.Texture;
            if (bg != null)
            {
                Graphics.LoadImage(bg);
                SetBackGroundImage(bg);
            }


            // バトルコモンの読み込み
            // Loading Battle Commons
            battleEvents = new BattleEventController();
            battleEvents.init(this, catalog, playerData, enemyData, owner.mapScene.mapEngine);
            battleEvents.playerLayouts = playerLayouts;
        }

        /// <summary>
        /// 状態での並び替え（射程使用のみ）
        /// Sort by state (Range use only)
        /// </summary>
        /// <param name="inIsPlayer">プレイヤーのパーティか？</param>
        /// <param name="inIsPlayer">party of players?</param>
        /// <returns>並びが変わったか？</returns>
        /// <returns>did the line change?</returns>
        bool SortTargetData(bool inIsPlayer)
        {
            if (!gameSettings.useBehindParty)
            {
                return false;
            }

            BattleCharacterBase[] list;

            if (gameSettings.usePositionBack)
            {
                list = inIsPlayer ? playerData.ToArray<BattleCharacterBase>() : enemyData.ToArray<BattleCharacterBase>();
            }
            else
            {
                list = inIsPlayer ? targetPlayerData.ToArray<BattleCharacterBase>() : targetEnemyData.ToArray<BattleCharacterBase>();
            }

            var targetList = new List<BattleCharacterBase>(list.Length);
            var behindList = new List<BattleCharacterBase>(list.Length);
            var deadList = new List<BattleCharacterBase>(list.Length);

            foreach (var item in list)
            {
                if (item.IsDeadCondition())
                {
                    deadList.Add(item);
                }
                else if (item.IsBehindPartyCondition())
                {
                    behindList.Add(item);
                }
                else
                {
                    targetList.Add(item);
                }
            }

            targetList.AddRange(behindList);
            targetList.AddRange(deadList);

            List<BattleCharacterBase> targetDataList = inIsPlayer ? targetPlayerData : targetEnemyData;

            for (int i = 0; i < targetList.Count; i++)
            {
                if (targetList[i] != targetDataList[i])
                {
                    targetDataList.Clear();
                    targetDataList.AddRange(targetList);

                    if (inIsPlayer)
                    {
                        playerViewData.Clear();

                        foreach (var item in targetList)
                        {
                            playerViewData.Add(item as BattlePlayerData);
                        }
                    }
                    else
                    {
                        enemyMonsterViewData.Clear();

                        foreach (var item in targetList)
                        {
                            enemyMonsterViewData.Add(item as BattleEnemyData);
                        }
                    }

                    return true;
                }
            }

            return false;
        }

        void UpdatePlayerPosition()
        {
            if (!SortTargetData(true))
            {
                return;
            }

            MovePlayerPosition();
        }

        public void MovePlayerPosition()
        {
            var playerLayouts = battleEvents.playerLayouts;

            for (int i = 0; i < targetPlayerData.Count; i++)
            {
                var player = targetPlayerData[i] as BattlePlayerData;
                Vector3 position;

                if ((playerLayouts == null) || (playerLayouts.Length <= i))
                {
                    position = BattleCharacterPosition.getPosition(BattleSequenceManagerBase.battleFieldCenter, BattleCharacterPosition.PosType.FRIEND, i, battlePlayerMax);
                }
                else
                {
                    position = BattleSequenceManagerBase.battleFieldCenter + playerLayouts[i];
                }

                var actor = Viewer.searchFromActors(player);

                actor.walk(position.X, position.Z, true);

                player.calcHeroLayout(playerData.Count);
            }
        }

        void UpdateEnemyPosition()
        {
            if (!SortTargetData(false))
            {
                return;
            }

            for (int i = 0; i < targetEnemyData.Count; i++)
            {
                var enemy = targetEnemyData[i] as BattleEnemyData;
                Vector3 position;

                if (monsterLayouts[i].IsLayoutValid())
                {
                    position = monsterLayouts[i].Layout + BattleSequenceManagerBase.battleFieldCenter;
                }
                else
                {
                    position = BattleCharacterPosition.getPosition(BattleSequenceManagerBase.battleFieldCenter, BattleCharacterPosition.PosType.ENEMY, i, battleEnemyMax);
                }

                var actor = Viewer.searchFromActors(enemy);

                actor.walk(position.X, position.Z, true);
            }
        }

        void UpdatePosition()
        {
            UpdatePlayerPosition();
            UpdateEnemyPosition();
        }

        internal void addVisibleEnemy(BattleEnemyData data)
        {
            enemyMonsterViewData.Add(data);

            var battleEnemyMax = enemyMonsterViewData.Count;

            for (int i = 0; i < battleEnemyMax; i++)
            {
                var enemy = enemyMonsterViewData[i];

                enemy.IsBattle = true;
                enemy.IsStock = false;

                if (!enemy.IsManualPosition)
                    enemy.SetPosition(BattleCharacterPosition.getPosition(BattleSequenceManagerBase.battleFieldCenter, BattleCharacterPosition.PosType.ENEMY, enemy.UniqueID - 1, battleEnemyMax));
            }
        }

        internal void removeVisibleEnemy(BattleEnemyData data)
        {
            enemyMonsterViewData.Remove(data);
        }

        internal void addVisiblePlayer(BattlePlayerData data)
        {
            playerViewData.Add(data);

            var battlePlayerMax = playerViewData.Count;

            for (int i = 0; i < battlePlayerMax; i++)
            {
                var player = playerViewData[i];

                player.IsBattle = true;
                player.IsStock = false;

                player.SetPosition(BattleCharacterPosition.getPosition(BattleSequenceManagerBase.battleFieldCenter, BattleCharacterPosition.PosType.FRIEND, i, battlePlayerMax));
                player.directionRad = -(float)Math.PI / 2; // Face UP/north (toward enemies)


                player.calcHeroLayout(playerData.Count);
            }
        }

        internal void removeVisiblePlayer(BattlePlayerData data)
        {
            playerViewData.Remove(data);

            var battlePlayerMax = playerViewData.Count;

            for (int i = 0; i < battlePlayerMax; i++)
            {
                var player = playerViewData[i];

                player.IsBattle = true;
                player.IsStock = false;

                player.SetPosition(BattleCharacterPosition.getPosition(BattleSequenceManagerBase.battleFieldCenter, BattleCharacterPosition.PosType.FRIEND, i, battlePlayerMax));
                player.directionRad = -(float)Math.PI / 2; // Face UP/north (toward enemies)


                player.calcHeroLayout(playerData.Count);
            }
        }

        public BattleEnemyData createEnemyData(Guid guid, Vector3? layout = null, int level = -1)
        {
            var data = new ExBattleEnemyData();

            if (layout != null)
            {
                data.pos =
                data.moveTargetPos = layout.Value + BattleSequenceManagerBase.battleFieldCenter;
                data.arrangmentType = BattleEnemyData.MonsterArrangementType.Manual;
            }

            var monster = catalog.getItemFromGuid<Rom.Cast>(guid);
            var monsterRes = catalog.getItemFromGuid(owner.IsBattle2D ? monster.graphic : monster.Graphics3D) as Common.Resource.GfxResourceBase;

            Common.Resource.Texture tex = null;

            if (monsterRes != null)
            {
                var r = monsterRes.gfxResourceId.getResource() as Common.Resource.SliceAnimationSet;

                if (r != null && r.items.Count > 0)
                {
                    tex = r.items[0].texture.getResource() as Common.Resource.Texture;
                }
            }

            data.monster = monster;
            data.EscapeSuccessBasePercent = 100;
            data.EscapeSuccessMessage = string.Format(gameSettings.glossary.battle_enemy_escape, monster.name);
            data.ExecuteCommandTurnCount = 1;
            data.image = monsterRes;
            data.imageId = tex;
            data.imageAlpha = 1.0f;

            data.IsBattle = true;
            data.IsStock = true;

            data.FriendPartyRefMember = targetEnemyData;
            data.EnemyPartyRefMember = targetPlayerData;

            data.battleStatusData = new BattleStatusWindowDrawer.StatusData();
            data.startStatusData = new BattleStatusWindowDrawer.StatusData();
            data.nextStatusData = new BattleStatusWindowDrawer.StatusData();

            if (level == -1)
            {
                //data.SetParameters(monster);
                var cast = Party.createHeroFromRom(catalog, owner.data.party, monster, 1, true);
                data.SetParameters(cast, party.getHeroName(monster.guId));
            }
            else
            {
                var cast = Party.createHeroFromRom(catalog, owner.data.party, monster, level, true);
                data.SetParameters(cast, party.getHeroName(monster.guId));
            }

            data.battleStatusData.statusValue.InitializeStatus(data.baseStatusValue);
            data.battleStatusData.consumptionStatusValue.InitializeStatus(data.consumptionStatusValue);
            data.startStatusData.statusValue.InitializeStatus(data.baseStatusValue);
            data.startStatusData.consumptionStatusValue.InitializeStatus(data.consumptionStatusValue);
            data.nextStatusData.statusValue.InitializeStatus(data.baseStatusValue);
            data.nextStatusData.consumptionStatusValue.InitializeStatus(data.consumptionStatusValue);

            data.battleStatusData.HitPoint = data.startStatusData.HitPoint = data.nextStatusData.HitPoint = data.HitPoint;
            data.battleStatusData.MagicPoint = data.startStatusData.MagicPoint = data.nextStatusData.MagicPoint = data.MagicPoint;

            return data;
        }

        public int SearchIndex(BattleCharacterBase self)
        {
            if (self is BattlePlayerData)
            {
                return playerData.IndexOf(self as BattlePlayerData);
            }
            else
            {
                return enemyData.IndexOf(self as BattleEnemyData);
            }
        }

        public BattleEnemyData addEnemyData(Guid guid, Vector3? layout = null, int index = -1, int level = -1)
        {
            var data = createEnemyData(guid, layout, level);

            stockEnemyData.Add(data);

            if (index < 0)
            {
                data.UniqueID = enemyData.Count + stockEnemyData.Count;
            }
            else
            {
                // まずは探して解放する
                // Find and release first
                var old = enemyData.FirstOrDefault(x => x.UniqueID == index);
                if (old != null)
                {
                    disposeEnemy(old);
                    enemyData.Remove(old);
                    targetEnemyData.Remove(old);
                }

                data.UniqueID = index;
            }

            return data;
        }


        public BattlePlayerData createPlayerData(Hero hero)
        {
            var data = new ExBattlePlayerData();

            var face = catalog.getItemFromGuid(hero.rom.face) as Resource.SliceAnimationSet;

            data.setFaceImage(face);

            data.player = hero;
            data.ExecuteCommandTurnCount = 1;

            data.EscapeSuccessBasePercent = 0;
            data.EscapeSuccessMessage = gameSettings.glossary.battle_escape;

            data.conditionInfoDic = new Dictionary<AttackAttributeType, Hero.ConditionInfo>(data.player.conditionInfoDic);
            data.battleStatusData = new BattleStatusWindowDrawer.StatusData();
            data.startStatusData = new BattleStatusWindowDrawer.StatusData();
            data.nextStatusData = new BattleStatusWindowDrawer.StatusData();

            data.SetParameters(hero, owner.debugSettings.battleHpAndMpMax, owner.debugSettings.battleStatusMax, party);

            data.startStatusData.statusValue.InitializeStatus(data.baseStatusValue);
            data.startStatusData.consumptionStatusValue.InitializeStatus(data.consumptionStatusValue);
            data.nextStatusData.statusValue.InitializeStatus(data.baseStatusValue);
            data.nextStatusData.consumptionStatusValue.InitializeStatus(data.consumptionStatusValue);

            data.startStatusData.HitPoint = data.nextStatusData.HitPoint = data.HitPoint;
            data.startStatusData.MagicPoint = data.nextStatusData.MagicPoint = data.MagicPoint;

            data.IsBattle = true;
            data.IsStock = true;

            // Pokemon-style: FriendPartyRefMember should include ALL party members (active + reserves) for targeting
            var allPartyMembersForRef = new List<BattleCharacterBase>();
            allPartyMembersForRef.AddRange(targetPlayerData);
            allPartyMembersForRef.AddRange(stockPlayerData);
            data.FriendPartyRefMember = allPartyMembersForRef;
            data.EnemyPartyRefMember = targetEnemyData;

            var layouts = catalog.getLayoutProperties();

            foreach (var id in hero.rom.battleCommandList)
			{
                var command = catalog.getItemFromGuid(id) as BattleCommand;

				switch (command.type)
				{
					case BattleCommand.CommandType.SKILLMENU:
                    case BattleCommand.CommandType.ITEMMENU:
						if ((command.refGuid != Guid.Empty) && (!layoutDic.ContainsKey(command.refGuid)))
						{
                            var usage = (command.type == BattleCommand.CommandType.SKILLMENU) ? Rom.LayoutProperties.LayoutNode.UsageInGame.BattleSkill : Rom.LayoutProperties.LayoutNode.UsageInGame.BattleItem;
                            var layout = owner.data.system.GetLayout(layouts, usage, command.refGuid);

							if (layout != null)
							{
                                var result = new BattleViewer3D.BattleUI.LayoutDrawerController();

                                result.LoadLayout(owner, catalog, layout, usage);
                                result.setBattleSequenceManager(this);

                                layoutDic.Add(command.refGuid, result);
                            }
                        }
                        break;
					default:
						break;
				}
            }

            return data;
        }

        public BattlePlayerData addPlayerData(Hero hero)
        {
            var data = createPlayerData(hero);

            data.UniqueID = idNumber;

            stockPlayerData.Add(data);

            idNumber++;

            return data;
        }

        public override void ReleaseImageData()
        {
            EffectPreloadJob.clearPreloads();
            battleEvents?.term();

            foreach (var player in playerData)
            {
                disposePlayer(player);
            }

            if (stockPlayerData != null)
            {
                foreach (var player in stockPlayerData)
                {
                    disposePlayer(player);
                }
            }



            foreach (var enemyMonster in enemyData)
            {
                disposeEnemy(enemyMonster);
            }

            if (stockEnemyData != null)
            {
                foreach (var enemyMonster in stockEnemyData)
                {
                    disposeEnemy(enemyMonster);
                }
            }



            foreach (var iconImageId in iconTable.Values)
            {
                Graphics.UnloadImage(iconImageId);
            }

            if (backGroundImageId != null)
            {
                Graphics.UnloadImage(backGroundImageId);
                backGroundImageId = null;//#23959 念のため初期化
            }

            battleViewer.ReleaseResourceData();
            resultViewer.ReleaseResourceData();

            BattleStartEvents = null;
            BattleResultWinEvents = null;
            BattleResultLoseGameOverEvents = null;
            BattleResultEscapeEvents = null;
        }

        private void disposePlayer(BattlePlayerData player)
        {
            if (player == null)
            {
                return;
            }

            player.disposeFace();

            if (player.positiveEffectDrawers != null)
            {
                foreach (var effectDrawer in player.positiveEffectDrawers) effectDrawer.finalize();
            }

            if (player.negativeEffectDrawers != null)
            {
                foreach (var effectDrawer in player.negativeEffectDrawers) effectDrawer.finalize();
            }

            if (player.statusEffectDrawers != null)
            {
                foreach (var effectDrawer in player.statusEffectDrawers) effectDrawer.finalize();
            }

            player.mapChr?.removeAllEffectDrawer();
        }

        private void disposeEnemy(BattleEnemyData enemyMonster)
        {
            if (enemyMonster == null)
            {
                return;
            }

            if (enemyMonster.positiveEffectDrawers != null)
            {
                foreach (var effectDrawer in enemyMonster.positiveEffectDrawers) effectDrawer.finalize();
            }

            if (enemyMonster.negativeEffectDrawers != null)
            {
                foreach (var effectDrawer in enemyMonster.negativeEffectDrawers) effectDrawer.finalize();
            }

            if (enemyMonster.statusEffectDrawers != null)
            {
                foreach (var effectDrawer in enemyMonster.statusEffectDrawers) effectDrawer.finalize();
            }

            enemyMonster.mapChr?.removeAllEffectDrawer();
        }

        public override void ApplyDebugSetting()
        {
            foreach (var player in playerData)
            {
                if (player.IsDeadCondition())
                {
                    player.conditionInfoDic.Clear();

                    player.ChangeEmotion(Resource.Face.FaceType.FACE_NORMAL);
                }

                player.SetParameters(player.player, owner.debugSettings.battleHpAndMpMax, owner.debugSettings.battleStatusMax, party);

                SetBattleStatusData(player);
            }
        }

		public override int CalcAttackWithWeaponDamage(BattleCharacterBase attacker, BattleCharacterBase target, AttackAttributeType attackAttribute, bool isCritical, Random battleRandom)
        {
            // DEBUG: Log when weapon damage calculation is called
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Damage Calc Debug", 
                string.Format("CalcAttackWithWeaponDamage called: {0} attacking {1}", attacker.Name, target.Name));
            
            float weaponDamage = (attacker.Attack) / 2.5f - (target.Defense) / ((isCritical) ? 8.0f : 4.0f);

            float elementDamage = 0;

            // Use the original Bakin calculation - type effectiveness now handled via b.edef in formulas
            elementDamage = attacker.ElementAttack * target.ResistanceAttackAttributePercent(attackAttribute);
            
            // DUAL-TYPE SYSTEM: Apply additional type effectiveness multiplier
            float dualTypeMultiplier = CalculateDualTypeEffectiveness(attacker, target);
            elementDamage *= dualTypeMultiplier;

            if (weaponDamage < 0)
                weaponDamage = 0;

            float totalDamage = (weaponDamage + elementDamage) * (1.0f - (float)battleRandom.NextDouble() / 10);

            // 武器がある？
            // Do you have a weapon?
            Rom.NItem weapon = null;
            Rom.Cast cast = null;
            if (attacker is BattlePlayerData pl)
            {
                cast = pl.player.rom;
                weapon = pl.player.equipments[0];
            }
            else if (attacker is BattleEnemyData enm)
            {
                cast = enm.monsterGameData.rom;
                weapon = enm.monsterGameData.equipments[0];
            }
            else if(attacker is MapCharacterBattleStatus mst)
            {
                cast = mst.source;
                weapon = catalog.getItemFromGuid<Rom.NItem>(mst.source.equipWeapon);
                if (mst.hero != null)
                    weapon = mst.hero.equipments[0];
            }

            // 式がある？
            // do you have a formula?
            string formula = null;
            if (cast != null && !string.IsNullOrEmpty(cast.formula))
            {
                // 素手の計算式
                // Bare hand calculation formula
                formula = cast.formula;
            }
            if (weapon != null)
            {
                // 武器が存在するので素手の計算式は使わない
                // Since there are weapons, we do not use the formula for calculating with bare hands.
                formula = null;
                if (!string.IsNullOrEmpty(weapon.weapon.formula))
                {
                    // 武器の計算式
                    // Weapon formula
                    formula = weapon.weapon.formula;
                }
            }

            if (formula != null)
            {
#if true
                totalDamage = EvalFormula(formula, attacker, target, attackAttribute, battleRandom);
#else
                totalDamage = EvalFormula(formula, attacker, target, (int)attackAttribute, battleRandom);
#endif
            }

            int damage = (int)(totalDamage * target.DamageRate * (isCritical ? attacker.GetCriticalScaleFactor(gameSettings) : 1.0f));

            if (damage < -attacker.MaxDamage) damage = -attacker.MaxDamage;
            if (damage > attacker.MaxDamage) damage = attacker.MaxDamage;

            return damage;
        }

        private int CalcAttackWithWeaponDamage(BattleCharacterBase attacker, BattleCharacterBase target, AttackAttributeType attackAttribute, bool isCritical, List<BattleDamageTextInfo> textInfo)
        {
            var damage = CalcAttackWithWeaponDamage(attacker, target, attackAttribute, isCritical, battleRandom);

            BattleDamageTextInfo.TextType textType = BattleDamageTextInfo.TextType.HitPointDamage;
            Guid statusId = Guid.Empty;

            if (damage < 0)
            {
                textType = BattleDamageTextInfo.TextType.Heal;
                statusId = catalog.getGameSettings().maxHPStatusID;
            }
            else if (isCritical)
            {
                textType = BattleDamageTextInfo.TextType.CriticalDamage;
            }

            AddBattleDamageTextInfo(textInfo, textType, target, Math.Abs(damage).ToString(), statusId);

            return damage;
        }

        public float EvalFormula(string formula, BattleCharacterBase attacker, BattleCharacterBase target, AttackAttributeType attackAttribute)
        {
            return EvalFormula(formula, attacker, target, attackAttribute, battleRandom);
        }

        private Tuple<BattleCharacterBase, List<BattleCharacterBase>> CreateBattleCharacterBaseParamTuple(BattleCharacterBase battleCharacter)
        {
            List<BattleCharacterBase> list = new List<BattleCharacterBase>();

            if (playerData.Contains(battleCharacter))
            {
                list.AddRange(playerData);

            }
            else if (enemyData.Contains(battleCharacter))
            {
                list.AddRange(enemyData);
            }

            return new Tuple<BattleCharacterBase, List<BattleCharacterBase>>(battleCharacter, list);
        }

        private float EvalFormula(string formula, BattleCharacterBase attacker, BattleCharacterBase target, AttackAttributeType attackAttribute, Random battleRandom)
        {
            // マップバトル対策
            // Map battle countermeasures
            if(playerData == null)
            {
                playerData = new List<BattlePlayerData>();
                enemyData = new List<BattleEnemyData>();
            }

            var attackerTuple = CreateBattleCharacterBaseParamTuple(attacker);
            var targetTuple = CreateBattleCharacterBaseParamTuple(target);
            var extendParamDic = new Dictionary<string, float>();

            extendParamDic.Add(Common.Rom.Formula.UserCountExtendKey, attackerTuple.Item2.Count);
            extendParamDic.Add(Common.Rom.Formula.UserAbleToActCountExtendKey, GetAbleActCount(attackerTuple.Item2));
            extendParamDic.Add(Common.Rom.Formula.TargetCountExtendKey, targetTuple.Item2.Count);
            extendParamDic.Add(Common.Rom.Formula.TargetAbleToActCountExtendKey, GetAbleActCount(targetTuple.Item2));
            extendParamDic.Add(Common.Rom.Formula.PlayerCountExtendKey, playerData.Count);
            extendParamDic.Add(Common.Rom.Formula.PlayerAbleToActCountExtendKey, GetAbleActCount(playerData));
            extendParamDic.Add(Common.Rom.Formula.EnemyCountExtendKey, enemyData.Count);
            extendParamDic.Add(Common.Rom.Formula.EnemyAbleToActCountExtendKey, GetAbleActCount(enemyData));
            extendParamDic.Add(Common.Rom.Formula.PlayerEscapeFailedCountExtendKey, playerEscapeFailedCount);

            return EvalFormula(formula, attackerTuple, targetTuple, attackAttribute, battleRandom, extendParamDic);
        }

		private int GetAbleActCount(System.Collections.IList list)
		{
            var cnt = list.Count;

            foreach (BattleCharacterBase item in list)
            {
                if (item.IsActionDisabled())
                {
                    cnt--;
                }
            }

            return cnt;
        }

#if true
        private static float EvalFormula(string formula, Tuple<BattleCharacterBase, List<BattleCharacterBase>> attackerTuple, Tuple<BattleCharacterBase, List<BattleCharacterBase>> targetTuple, AttackAttributeType attackAttribute, Random battleRandom, Dictionary<string, float> extendParamDic)
#else
        private static float EvalFormula(string formula, BattleCharacterBase attacker, BattleCharacterBase target, int attackAttribute, Random battleRandom)
#endif
        {
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Attack Formula", formula);

            // 式をパースして部品に分解する
            // Parse an expression and break it down into parts
            var words = Util.ParseFormula(formula);

            // 逆ポーランド記法に並べ替える
            // Sort to Reverse Polish Notation
            words = Util.SortToRPN(words);

            return CalcRPN(words, attackerTuple, targetTuple, attackAttribute, battleRandom, extendParamDic);
        }

        internal static float GetRandom(Random battleRandom, float max, float min)
        {
            if (min > max)
            {
                var tmp = min;
                min = max;
                max = tmp;
            }

            return (float)battleRandom.NextDouble() * (max - min) + min;
        }

#if true
        public static float CalcRPN(List<string> words, Tuple<BattleCharacterBase, List<BattleCharacterBase>> attackerTuple, Tuple<BattleCharacterBase, List<BattleCharacterBase>> targetTuple,
            AttackAttributeType attackAttribute, Random battleRandom, Dictionary<string, float> extendParamDic = null)
#else
        public static float CalcRPN(List<string> words, BattleCharacterBase attacker, BattleCharacterBase target, int attackAttribute, Random battleRandom)
#endif
        {
            var stack = new Stack<float>();
            stack.Push(0);

            float a, b;
            foreach (var word in words)
            {
                switch (word)
                {
                    case "min":
                        stack.Push(Math.Min(stack.Pop(), stack.Pop()));
                        break;
                    case "max":
                        stack.Push(Math.Max(stack.Pop(), stack.Pop()));
                        break;
                    case Common.Rom.Formula.ClampSpecifyKey:
                        stack.Push(MathHelper.Clamp(stack.Pop(), stack.Pop(), stack.Pop()));
                        break;
                    case "rand":
                        a = stack.Pop();
                        b = stack.Pop();
                        stack.Push(GetRandom(battleRandom, Math.Max(a, b), Math.Min(a, b)));
                        break;
                    case Rom.Formula.RoundSpecifyKey:
                        stack.Push((float)Math.Round(stack.Pop()));
                        break;
                    case Rom.Formula.CeilSpecifyKey:
                        stack.Push((float)Math.Ceiling(stack.Pop()));
                        break;
                    case Rom.Formula.FloorSpecifyKey:
                        stack.Push((float)Math.Floor(stack.Pop()));
                        break;
                    case "*":
                        stack.Push(stack.Pop() * stack.Pop());
                        break;
                    case "/":
                        a = stack.Pop();
                        try
                        {
                            stack.Push(stack.Pop() / a);
                        }
                        catch (DivideByZeroException e)
                        {
#if WINDOWS
                            System.Windows.Forms.MessageBox.Show(e.Message);
#endif
                            stack.Push(0);
                        }
                        break;
                    case "%":
                        a = stack.Pop();
                        try
                        {
                            stack.Push(stack.Pop() % a);
                        }
                        catch (DivideByZeroException e)
                        {
#if WINDOWS
                            System.Windows.Forms.MessageBox.Show(e.Message);
#endif
                            stack.Push(0);
                        }
                        break;
                    case "+":
                        stack.Push(stack.Pop() + stack.Pop());
                        break;
                    case "-":
                        a = stack.Pop();
                        stack.Push(stack.Pop() - a);
                        break;
                    case ",":
                        break;
                    default:
                        // 数値や変数はスタックに積む
                        // Put numbers and variables on the stack

                        float num;
                        if (float.TryParse(word, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out num))
                        {
                            // 数値
                            // numerical value
                            stack.Push(num);
                        }
                        else
                        {
                            if (extendParamDic?.ContainsKey(word) ?? false)
                            {
                                stack.Push(extendParamDic[word]);
                            }
                            else
                            {
                                // 変数
                                // variable
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "FORMULA DEBUG", 
                                    string.Format("🔍 Parsing variable: '{0}'", word));
                                float result = parseBattleNum(word, attackerTuple, targetTuple, attackAttribute);
                                stack.Push(result);
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "FORMULA DEBUG", 
                                    string.Format("📊 Variable '{0}' = {1}", word, result));
                            }

                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Attack Formula",
                                string.Format("Parse Result / {0} : {1}", word, stack.Peek()));
                        }
                        break;
                }
            }

            return stack.Pop();
        }

#if true
        private static float parseBattleNum(string word, Tuple<BattleCharacterBase, List<BattleCharacterBase>> attackerTuple, Tuple<BattleCharacterBase, List<BattleCharacterBase>> targetTuple, AttackAttributeType attackAttribute)
#else
        private static float parseBattleNum(string word, BattleCharacterBase attacker, BattleCharacterBase target, int attackAttribute)
#endif
        {
            var objectType = Rom.Formula.SpecifyType.None;
            var funcType = Rom.Formula.SpecifyType.None;
            var statusKey = "";

            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PARSE DEBUG", 
                string.Format("🔍 Before SplitWord: '{0}'", word));

            // Special case for edef - handle before SplitWord
            if (word == "edef")
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "EDEF FORMULA", 
                    "🎯 EDEF special case triggered!");
                float result = GetSimpleTypeEffectiveness(attackerTuple.Item1, targetTuple.Item1);
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "EDEF FORMULA", 
                    string.Format("📊 EDEF result: {0}x", result));
                return result;
            }

            // Special case for weather - handle before SplitWord
            if (word == "weather")
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "WEATHER FORMULA", 
                    "🌤️ WEATHER special case triggered!");
                float result = GetWeatherMultiplier(attackerTuple.Item1, targetTuple.Item1);
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "WEATHER FORMULA", 
                    string.Format("🌤️ WEATHER result: {0}x", result));
                return result;
            }

            if (Rom.Formula.SplitWord(word, ref objectType, ref funcType, ref statusKey))
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PARSE DEBUG", 
                    string.Format("📊 After SplitWord: objectType={0}, funcType={1}, statusKey='{2}'", objectType, funcType, statusKey));
                List<BattleCharacterBase> srcList = new List<BattleCharacterBase>();

                if (objectType == Rom.Formula.SpecifyType.User)
                {
                    if (attackerTuple != null)
                    {
                        if (funcType == Rom.Formula.SpecifyType.None)
                        {
                            srcList.Add(attackerTuple?.Item1);
                        }
                        else
                        {
                            srcList.AddRange(attackerTuple?.Item2);
                        }
                    }
                }
                else
                {
                    if (targetTuple != null)
                    {
                        if (funcType == Rom.Formula.SpecifyType.None)
                        {
                            srcList.Add(targetTuple?.Item1);
                        }
                        else
                        {
                            srcList.AddRange(targetTuple?.Item2);
                        }
                    }
                }

                var cnt = 0;
                var ret = (funcType == Rom.Formula.SpecifyType.Min) ? float.MaxValue : 0f;

				foreach (var src in srcList)
                {
                    if (src == null)
                    {
                        continue;
                    }

                    var num = 0f;

                    switch (statusKey)
                    {
                        case "lv":
                            if (src is BattlePlayerData)
                            {
                                num = ((BattlePlayerData)src).player.level;
                            }
                            else if (src is BattleEnemyData)
                            {
                                num = ((BattleEnemyData)src).monsterGameData.level;
                            }
                            else
                            {
                                num = 1;
                            }
                            break;
                        case "hp":
                            num = src.HitPoint;
                            break;
                        case "mp":
                            num = src.MagicPoint;
                            break;
                        case "mhp":
                            num = src.MaxHitPoint;
                            break;
                        case "mmp":
                            num = src.MaxMagicPoint;
                            break;
                        case "atk":
                            num = src.Attack;
                            break;
                        case "def":
                            num = src.Defense;
                            break;
                        case "spd":
                            num = src.Speed;
                            break;
                        case "mgc":
                            num = src.Magic;
                            break;
                        case "eatk":
                            num = src.ElementAttack;
                            break;
                        case "edef":
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "EDEF FORMULA", 
                                string.Format("🎯 EDEF called in formula for {0}", src.Name));
                            // Use simplified type effectiveness for formula context
                            num = GetSimpleTypeEffectiveness(attackerTuple.Item1, src);
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "EDEF FORMULA", 
                                string.Format("📊 EDEF result: {0}x", num));
                            break;
                        default:
                            num = src.GetStatus(Common.Catalog.sInstance.getGameSettings(), statusKey);
                            break;
                    }

                    switch (funcType)
                    {
                        case Rom.Formula.SpecifyType.Min:
                            ret = Math.Min(num, ret);
                            break;
                        case Rom.Formula.SpecifyType.Max:
                            ret = Math.Max(num, ret);
                            break;
                        case Rom.Formula.SpecifyType.Sum:
                        case Rom.Formula.SpecifyType.Avg:
                            ret += num;
                            break;
                        default:
                            ret = num;
                            break;
                    }

                    cnt++;
                }

				if (cnt > 0)
				{
                    return (funcType == Rom.Formula.SpecifyType.Avg) ? ret / cnt : ret;
                }
            }

            if (word.StartsWith("\\$["))
            {
                var valName = word.Substring(3, word.Length - 4);
                return (float)GameMain.instance.data.system.GetVariable(valName);
            }

            return 0;
        }

        private void AddBattleDamageTextInfo(List<BattleDamageTextInfo> inTextInfo, BattleDamageTextInfo.TextType inTextType, BattleCharacterBase inTarget, string inText, System.Guid inStatusId)
        {
            var color = Color.White;

            if (inStatusId != Guid.Empty)
            {
                var damage = true;

                switch (inTextType)
                {
                    case BattleDamageTextInfo.TextType.HitPointHeal:
                    case BattleDamageTextInfo.TextType.MagicPointHeal:
                    case BattleDamageTextInfo.TextType.Heal:
                        damage = false;
                        break;
                    default:
                        break;
                }

                color = catalog.getGameSettings().GetStatusColor(inStatusId, damage);
            }
            else
            {
				switch (inTextType)
				{
					case BattleDamageTextInfo.TextType.HitPointDamage:
                        color = catalog.getGameSettings().GetStatusColor(Rom.GameSettings.HPStatusID, true);
                        break;
					case BattleDamageTextInfo.TextType.CriticalDamage:
                        color = catalog.getGameSettings().GetCriticalColor();
                        break;
					case BattleDamageTextInfo.TextType.Heal:
						break;
					default:
						break;
				}
            }

            if (color.A != 0x00)
            {
                inTextInfo.Add(new BattleDamageTextInfo(inTextType, inTarget, inText, inStatusId, color));
            }
        }

        private void AddBattleMissTextInfo(List<BattleDamageTextInfo> inTextInfo, BattleCharacterBase inTarget)
        {
            inTextInfo.Add(new BattleDamageTextInfo(BattleDamageTextInfo.TextType.Miss, inTarget, gameSettings.glossary.battle_miss));
        }

        private bool Heal(BattleCharacterBase target, Rom.EffectParamSettings effectParamSettings, bool canGuard, Dictionary<Guid, string> formulaDic, List<BattleDamageTextInfo> textInfo,
            Guid attribute, BattleCharacterBase effecter = null, Dictionary<Guid, int> totalHealValueDic = null, Dictionary<Guid, Rom.DamageDrainEffectParam> drainPercentDic = null)
        {
            var isEffect = false;
            var useSkill = effecter != null;
            var effectValueDic = new Dictionary<Guid, int>();

            foreach (var effectParam in effectParamSettings.GetConsumptionStatusValueChangeEffectParamList())
            {
                if (effectParam.ChangeParam == 0)
                {
                    continue;
                }

                var info = gameSettings.GetCastStatusParamInfo(effectParam.Id);

                if (info == null)
                {
                    continue;
                }

                var value = 0;

                switch (effectParam.ChangeType)
                {
                    case Util.StatusChangeType.Unknwon:
                        break;
                    case Util.StatusChangeType.Direct:
                        value = effectParam.ChangeParam;
                        break;
                    case Util.StatusChangeType.Percent:
                        {
                            var baseStatus = (useSkill && (effectParam.BaseStatusId != info.guId)) ? effecter : target;

                            value = baseStatus.GetSystemStatus(gameSettings, effectParam.BaseStatusId) * effectParam.ChangeParam / 100;
                        }
                        break;
                    default:
                        break;
                }

                if (!effectValueDic.ContainsKey(info.guId))
                {
                    effectValueDic[info.guId] = 0;
                }

                effectValueDic[info.guId] += value;
            }

            foreach (var key in effectValueDic.Keys.ToArray())
            {
                var effectValue = effectValueDic[key];

                if (effectValue >= 0)
                {
                    continue;
                }

                var info = gameSettings.GetCastStatusParamInfo(key);

                if (info.Consumption && (effecter != null))
                {
                    // ダメージなので属性耐性を考慮
                    // Since it is damage, consider attribute resistance.
                    var effectValueTmp = effectValue * target.ResistanceAttackAttributePercent(attribute);

                    if (canGuard && (info.guId == gameSettings.maxHPStatusID))
                    {
                        effectValueTmp *= target.DamageRate;
                    }

                    effectValueDic[info.guId] = (int)effectValueTmp;
                }
            }

            foreach (var item in formulaDic)
            {
                var info = gameSettings.GetCastStatusParamInfo(item.Key);

                if (info == null)
                {
                    continue;
                }

                if (!effectValueDic.ContainsKey(info.guId))
                {
                    effectValueDic[info.guId] = 0;
                }

                effectValueDic[info.guId] += (int)EvalFormula(item.Value, effecter, target, attribute, battleRandom);
            }

            var minHeal = Hero.MIN_STATUS;
            var maxHeal = Hero.MAX_STATUS;

			if (useSkill)
			{
                maxHeal = effecter.MaxDamage;
                minHeal = -maxHeal;
            }

            foreach (var item in effectValueDic)
            {
                var info = gameSettings.GetCastStatusParamInfo(item.Key);

                if (info == null)
                {
                    continue;
                }

                var effectValue = Math.Max(Math.Min(item.Value, maxHeal), minHeal);

                BattleDamageTextInfo.TextType textType;
                var heal = effectValue > 0;

                if (useSkill)
                {
                    // ドレイン用加算
                    // Addition for drain
                    if (!heal && drainPercentDic.ContainsKey(info.ConsumptionId))
                    {
                        effectValue = -Math.Min(-effectValue, target.consumptionStatusValue.GetStatus(item.Key));
                    }

                    if (!totalHealValueDic.ContainsKey(info.ConsumptionId))
                    {
                        totalHealValueDic[info.ConsumptionId] = 0;
                    }

                    totalHealValueDic[info.ConsumptionId] += effectValue;
                }

                target.consumptionStatusValue.AddStatus(item.Key, effectValue);

                switch (info.Key)
                {
                    case Rom.GameSettings.MaxHPStatusKey:
                        target.HitPoint += effectValue;

                        if ((target.HitPoint > 0) && target.IsDeadCondition())
                        {
                            target.Resurrection(battleEvents);
                        }

                        target.ConsistancyHPPercentConditions(catalog, battleEvents);

                        if (!heal && useSkill)
                        {
                            SetCounterAction(target, effecter);
                        }
                        break;
                    case Rom.GameSettings.MaxMPStatusKey:
                        target.MagicPoint += effectValue;
                        break;
                    default:
                        break;
                }

                if (heal)
                {
                    textType = BattleDamageTextInfo.TextType.Heal;
                }
                else
                {
                    textType = BattleDamageTextInfo.TextType.Damage;
                    effectValue = Math.Abs(effectValue);
                }

                AddBattleDamageTextInfo(textInfo, textType, target, effectValue.ToString(), item.Key);

                isEffect = true;
            }

            return isEffect;
        }

        private void EffectSkill(BattleCharacterBase effecter, Rom.NSkill skill, BattleCharacterBase[] friendEffectTargets, BattleCharacterBase[] enemyEffectTargets,
            List<BattleDamageTextInfo> textInfo, List<RecoveryStatusInfo> recoveryStatusInfo, out BattleCharacterBase[] friend, out BattleCharacterBase[] enemy,
            out ReflectionInfo[] reflections, bool checkReflection)
        {
            // DEBUG: Track skill execution
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "SKILL EXEC", 
                string.Format("🚀 SKILL EXECUTED: {0} uses {1}", effecter?.Name ?? "null", skill?.name ?? "null"));
            
            // FORCE TEST TYPE EFFECTIVENESS for any skill with "psychic" in the name
            if (skill?.name?.ToLowerInvariant().Contains("psychic") == true && enemyEffectTargets?.Length > 0)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "FORCE TEST", 
                    "🧪 FORCING TYPE EFFECTIVENESS TEST FOR PSYCHIC SKILL");
                var testMultiplier = CalculateDualTypeEffectiveness(effecter, enemyEffectTargets[0]);
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "FORCE TEST", 
                    string.Format("🧪 FORCED TEST RESULT: {0}x multiplier", testMultiplier));
            }
            
            var gs = catalog.getGameSettings();
            var friendEffect = skill.friendEffect;
            var enemyEffect = skill.enemyEffect;
            var useOldEffectParam = false;
            var option = skill.option;
            int totalHitPointDamage = 0;
            int totalMagicPointDamage = 0;
            var friendTotalSkillValueDic = new Dictionary<Guid, int>();
            var friendSkillFormulaDic = new Dictionary<Guid, string>();
            var friendSkillAttribute = Guid.Empty;
            var friendSkillDrainPercentDic = new Dictionary<Guid, Rom.DamageDrainEffectParam>();
            var enemyTotalSkillDamageDic = new Dictionary<Guid, int>();
            var enemySkillFormulaDic = new Dictionary<Guid, string>();
            var enemySkillAttribute = Guid.Empty;
            var enemySkillDrainPercentDic = new Dictionary<AttackAttributeType, Rom.DamageDrainEffectParam>();
            var reflectionInfos = new List<ReflectionInfo>();

            if (friendEffect != null)
            {
                foreach (var item in friendEffect.EffectParamSettings.GetEtcEffectParamList())
                {
                    switch (item.Type)
                    {
                        case Rom.EffectParamBase.EffectType.ConsumptionValueFormula:
                            if (item is Rom.ConsumptionValueFormulaEffectParam formula && !string.IsNullOrEmpty(formula.Formula))
                            {
                                friendSkillFormulaDic[formula.TargetId] = formula.Formula;
                            }
                            break;
                        case Rom.EffectParamBase.EffectType.SkillAttribute:
                            if (item is Rom.SkillAttributeEffectParam damageAttribute)
                            {
                                friendSkillAttribute = damageAttribute.Id;

                                if (friendSkillAttribute == Rom.SkillAttributeEffectParam.WeaponAttributeId)
                                {
                                    friendSkillAttribute = effecter.Hero?.equipments[Hero.WEAPON_INDEX]?.AttackAttribute ?? Guid.Empty;
                                }
                            }
                            break;
                        case Rom.EffectParamBase.EffectType.DamageDrain:
                            if (item is Rom.DamageDrainEffectParam damageDrain && (damageDrain.Percent != 0))
                            {
                                friendSkillDrainPercentDic.Add(damageDrain.TargetId, damageDrain);
                            }
                            break;
                        default:
                            break;
                    }
                }
            }

            if (enemyEffect != null)
            {
                foreach (var item in enemyEffect.EffectParamSettings.GetEtcEffectParamList())
                {
                    switch (item.Type)
                    {
                        case Rom.EffectParamBase.EffectType.ConsumptionValueFormula:
                            if (item is Rom.ConsumptionValueFormulaEffectParam damageFormula && !string.IsNullOrEmpty(damageFormula.Formula))
                            {
                                enemySkillFormulaDic[damageFormula.TargetId] = damageFormula.Formula;
                            }
                            break;
                        case Rom.EffectParamBase.EffectType.SkillAttribute:
                            if (item is Rom.SkillAttributeEffectParam damageAttribute)
                            {
                                enemySkillAttribute = damageAttribute.Id;
                                Console.WriteLine($"SKILL ATTR DEBUG: Initial attribute={enemySkillAttribute}");

								if (enemySkillAttribute == Rom.SkillAttributeEffectParam.WeaponAttributeId)
								{
                                    enemySkillAttribute = effecter.Hero?.equipments[Hero.WEAPON_INDEX]?.AttackAttribute ?? Guid.Empty;
                                    Console.WriteLine($"SKILL ATTR DEBUG: Using weapon attribute={enemySkillAttribute}");
                                }
                                else
                                {
                                    Console.WriteLine($"SKILL ATTR DEBUG: Using skill attribute={enemySkillAttribute}");
                                }
                            }
                            break;
                        case Rom.EffectParamBase.EffectType.DamageDrain:
                            if ((item is Rom.DamageDrainEffectParam damageDrain) && (damageDrain.Percent != 0))
                            {
                                enemySkillDrainPercentDic.Add(damageDrain.TargetId, damageDrain);
                            }
                            break;
                        default:
                            break;
                    }
                }
            }

            var friendEffectCharacters = new List<BattleCharacterBase>();
            var enemyEffectCharacters = new List<BattleCharacterBase>();

            foreach (var target in friendEffectTargets)
            {
                bool isEffect = false;
                bool isReflection = false;

                var reflect = target.GetReflectionParam(skill, true);
                if (checkReflection && reflect != null && reflect.Percent > battleRandom.Next(100))
                {
                    #region 反射
                    isReflection = true;
                    reflectionInfos.Add(new ReflectionInfo(true, target, reflect));
                    #endregion
                }
                else
                {
                    #region 味方効果発動
                    // 戦闘不能状態ならばHPとMPを回復させない (スキル効果に「戦闘不能状態を回復」がある場合のみ回復効果を有効とする)
                    // Does not recover HP and MP if incapacitated (Recovery effect is valid only if the skill effect has \
                    // |------------------------====----------------|--------------|----------------|
                    // | ↓スキル効果              効果対象の状態→ | 戦闘不能状態 | それ以外の状態 |
                    // | ↓Skill effect Target status → | Incapacitated state | Other states |
                    // |--------------------------------------------|--------------|----------------|
                    // | 「戦闘不能者のみ有効」あり「即死回復」あり |     有効     |      無効      |
                    // | \
                    // | 「戦闘不能者のみ有効」あり「即死回復」なし |     無効     |      有効      |
                    // | \
                    // | 「戦闘不能者のみ有効」なし「即死回復」あり |     有効     |      有効      |
                    // | \
                    // | 「戦闘不能者のみ有効」なし「即死回復」なし |     無効     |      有効      |
                    // | \
                    // |--------------------------------------------|--------------|----------------|
                    bool isHealParameter = false;

                    // 即死回復かつ即死状態だったら有効
                    // Effective if instant death recovery and instant death state
                    if (friendEffect.HasDeadCondition(catalog) && target.IsDeadCondition())
                        isHealParameter = true;

                    // 即死回復なし で 生存状態であれば有効
                    // Effective if there is no instant death recovery and you are alive
                    if (!friendEffect.HasDeadCondition(catalog) && !target.IsDeadCondition())
                        isHealParameter = true;

                    // 即死回復ありでも、戦闘不能者のみ有効がオフなら全員に効果あり
                    // Even if there is instant death recovery, it is effective for everyone if only effective for incapacitated is off
                    if (friendEffect.HasDeadCondition(catalog) && !skill.option.onlyForDown)
                        isHealParameter = true;

                    if (friendEffect.EffectParamSettings.UseOldEffectParam)
                    {
                        useOldEffectParam = true;

                        // HitPoint 回復 or ダメージ
                        // HitPoint recovery or damage
                        if ((friendEffect.hitpoint != 0 || friendEffect.hitpointPercent != 0 ||
                            friendEffect.hitpoint_powerPercent != 0 || friendEffect.hitpoint_magicPercent != 0 ||
                            !string.IsNullOrEmpty(friendEffect.hitpointFormula)) && isHealParameter)
                        {
                            int effectValue = friendEffect.hitpoint + (int)(friendEffect.hitpointPercent / 100.0f * target.MaxHitPoint) + (int)(friendEffect.hitpoint_powerPercent / 100.0f * effecter.Attack) + (int)(friendEffect.hitpoint_magicPercent / 100.0f * effecter.Magic);

                            if (effectValue > effecter.MaxDamage)
                                effectValue = effecter.MaxDamage;
                            if (effectValue < -effecter.MaxDamage)
                                effectValue = -effecter.MaxDamage;

                            if (effectValue >= 0)
                            {
                                // 回復効果の場合は属性耐性の計算を行わない
                                // Attribute resistance is not calculated for recovery effects
                            }
                            else
                            {
#if true
                                switch (target.AttackAttributeTolerance(friendEffect.AttributeGuid))
#else
                        switch (target.AttackAttributeTolerance(friendEffect.attribute))
#endif
                                {
                                    case AttributeToleranceType.Normal:
                                    case AttributeToleranceType.Strong:
                                    case AttributeToleranceType.Weak:
                                        {
#if true
                                            float effectValueTmp = effectValue * target.ResistanceAttackAttributePercent(skill.friendEffect.AttributeGuid) * target.DamageRate;
#else
                                    float effectValueTmp = effectValue * target.ResistanceAttackAttributePercent(skill.friendEffect.attribute) * target.DamageRate;
#endif
                                            if (effectValueTmp > effecter.MaxDamage)
                                                effectValueTmp = effecter.MaxDamage;
                                            if (effectValueTmp < -effecter.MaxDamage)
                                                effectValueTmp = -effecter.MaxDamage;
                                            effectValue = (int)effectValueTmp;
                                        }
                                        break;

                                    case AttributeToleranceType.Absorb:
                                        {
#if true
                                            float effectValueTmp = effectValue * target.ResistanceAttackAttributePercent(skill.friendEffect.AttributeGuid) * target.DamageRate;
#else
                                    float effectValueTmp = effectValue * target.ResistanceAttackAttributePercent(skill.friendEffect.attribute) * target.DamageRate;
#endif
                                            if (effectValueTmp > effecter.MaxDamage)
                                                effectValueTmp = effecter.MaxDamage;
                                            if (effectValueTmp < -effecter.MaxDamage)
                                                effectValueTmp = -effecter.MaxDamage;
                                            effectValue = (int)effectValueTmp;
                                        }
                                        break;

                                    case AttributeToleranceType.Invalid:
                                        effectValue = 0;
                                        break;
                                }

                            }

                            // 式がある？
                            // do you have a formula?
                            if (!string.IsNullOrEmpty(friendEffect.hitpointFormula))
                            {
#if true
                                effectValue += (int)EvalFormula(friendEffect.hitpointFormula, effecter, target, friendEffect.AttributeGuid, battleRandom);
#else
                        effectValue += (int)EvalFormula(friendEffect.hitpointFormula, effecter, target, friendEffect.attribute, battleRandom);
#endif
                                if (effectValue > effecter.MaxDamage)
                                    effectValue = effecter.MaxDamage;
                                if (effectValue < -effecter.MaxDamage)
                                    effectValue = -effecter.MaxDamage;
                            }

                            target.HitPoint += effectValue;
                            target.consumptionStatusValue.AddStatus(gs.maxHPStatusID, effectValue);

                            if (target.HitPoint > 0)
                            {
                                if (target.IsDeadCondition())
                                {
                                    target.Resurrection(battleEvents);
                                }
                            }

                            target.ConsistancyHPPercentConditions(catalog, battleEvents);

                            BattleDamageTextInfo.TextType textType;
                            var heal = effectValue > 0;

                            if (heal)
                            {
                                textType = BattleDamageTextInfo.TextType.Heal;
                            }
                            else
                            {
                                textType = BattleDamageTextInfo.TextType.Damage;
                                effectValue = Math.Abs(effectValue);
                                totalHitPointDamage += effectValue;
                                SetCounterAction(target, effecter);
                            }

                            AddBattleDamageTextInfo(textInfo, textType, target, effectValue.ToString(), gs.maxHPStatusID);

                            isEffect = true;
                        }

                        // MagicPoint 回復
                        // MagicPoint Recovery
                        if ((friendEffect.magicpoint != 0 || friendEffect.magicpointPercent != 0) && isHealParameter)
                        {
                            int effectValue = ((friendEffect.magicpoint) + (int)(friendEffect.magicpointPercent / 100.0f * target.MaxMagicPoint));

                            if (effectValue > effecter.MaxDamage)
                                effectValue = effecter.MaxDamage;
                            if (effectValue < -effecter.MaxDamage)
                                effectValue = -effecter.MaxDamage;

                            target.MagicPoint += effectValue;

                            var info = gameSettings.GetCastStatusParamInfo(gs.maxMPStatusID, true);

                            if (info != null)
                            {
                                target.consumptionStatusValue.AddStatus(info.guId, effectValue);
                            }

                            BattleDamageTextInfo.TextType textType;
                            var heal = effectValue > 0;

                            if (heal)
                            {
                                textType = BattleDamageTextInfo.TextType.MagicPointHeal;
                            }
                            else
                            {
                                textType = BattleDamageTextInfo.TextType.MagicPointDamage;
                                effectValue = Math.Abs(effectValue);
                                totalMagicPointDamage += effectValue;
                            }

                            AddBattleDamageTextInfo(textInfo, textType, target, effectValue.ToString(), gs.maxMPStatusID);

                            isEffect = true;
                        }
                    }
                    else if (isHealParameter)
                    {
                        Heal(target, friendEffect.EffectParamSettings, skill.CanGuard, friendSkillFormulaDic, textInfo, friendSkillAttribute, effecter, friendTotalSkillValueDic, friendSkillDrainPercentDic);

                        isEffect = true;
                    }

                    // 状態異常回復
                    // status ailment recovery
                    conditionRecoveryImpl(friendEffect.RecoveryList, target, ref isEffect);
                    bool isDisplayMiss = false;
                    conditionAssignImpl(friendEffect.AssignList, target, ref isEffect, ref isDisplayMiss);
                    if (isDisplayMiss)
                    {
                        // 状態異常付与に失敗した時missと出す場合はコメントアウトを外す
                        // Remove the comment out if you want to output a miss when you fail to apply the status ailment
                        //AddBattleMissTextInfo(textInfo, target);
                    }

                    // パラメータ変動
                    // Parameter variation
                    if (target.enhanceStatusValue.InitializeEnhancementEffectStatus(target.baseStatusValue.GetCalcEffectStatuses(friendEffect.EffectParamSettings), true))
                    {
                        isEffect = true;
                    }

                    var enhancement = target.enhanceStatusValue.GetSystemStatus(gameSettings, gs.maxHPStatusID);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.MaxHitPointEnhance))      // 腕力 / strength
                    {
                        target.MaxHitPointEnhance = enhancement;

                        isEffect = true;
                    }

                    enhancement = target.enhanceStatusValue.GetSystemStatus(gameSettings, gs.maxMPStatusID);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.MaxMagicPointEnhance))      // 腕力 / strength
                    {
                        target.MaxMagicPointEnhance = enhancement;

                        isEffect = true;
                    }

                    enhancement = Util.CalcStatusChangeValue(target.PowerBase, friendEffect.power, friendEffect.powerStatusChangeType);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.PowerEnhancement))      // 腕力 / strength
                    {
                        target.PowerEnhancement = enhancement;

                        isEffect = true;
                    }

                    enhancement = Util.CalcStatusChangeValue(target.VitalityBase, friendEffect.vitality, friendEffect.vitalityStatusChangeType);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.VitalityEnhancement))// 体力 / physical strength
                    {
                        target.VitalityEnhancement = enhancement;

                        isEffect = true;
                    }

                    enhancement = Util.CalcStatusChangeValue(target.MagicBase, friendEffect.magic, friendEffect.magicStatusChangeType);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.MagicEnhancement))      // 魔力 / magical power
                    {
                        target.MagicEnhancement = enhancement;

                        isEffect = true;
                    }

                    enhancement = Util.CalcStatusChangeValue(target.SpeedBase, friendEffect.speed, friendEffect.speedStatusChangeType);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.SpeedEnhancement))       // 素早さ / Agility
                    {
                        target.SpeedEnhancement = enhancement;

                        isEffect = true;
                    }

                    enhancement = Util.CalcStatusChangeValue(target.DexterityBase, friendEffect.dexterity, friendEffect.dexterityStatusChangeType);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.DexterityEnhancement))   // 命中 / hit
                    {
                        target.DexterityEnhancement = enhancement;

                        isEffect = true;
                    }

                    enhancement = Util.CalcStatusChangeValue(target.EvasionBase, friendEffect.evasion, friendEffect.evasionStatusChangeType);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.EvasionEnhancement)) // 回避 / Avoidance
                    {
                        target.EvasionEnhancement = enhancement;

                        isEffect = true;
                    }

                    // 各属性耐性
                    // Each attribute resistance
#if true
                    foreach (var ai in friendEffect.AttrDefenceList)
                    {
                        if ((ai.value != 0) && (ai.attribute != Guid.Empty) &&
                            (!target.ResistanceAttackAttributeEnhance.ContainsKey(ai.attribute) ||
                            Math.Abs(ai.value) >= Math.Abs(target.ResistanceAttackAttributeEnhance[ai.attribute])))
                        {
                            target.ResistanceAttackAttributeEnhance[ai.attribute] = ai.value;
                            isEffect = true;
                        }
                    }
                    foreach (var ci in friendEffect.ConditionDefenceList)
                    {
                        if ((ci.value != 0) && (ci.condition != Guid.Empty) &&
                            (!target.ResistanceAilmentEnhance.ContainsKey(ci.condition) ||
                            Math.Abs(ci.value) >= Math.Abs(target.ResistanceAilmentEnhance[ci.condition])))
                        {
                            target.ResistanceAilmentEnhance[ci.condition] = ci.value;
                            isEffect = true;
                        }
                    }
#else
                if (friendEffect.attrAdefense != 0 && Math.Abs(friendEffect.attrAdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.A]))
                {
                    target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.A] = friendEffect.attrAdefense;

                    isEffect = true;
                }
                if (friendEffect.attrBdefense != 0 && Math.Abs(friendEffect.attrAdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.B]))
                {
                    target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.B] = friendEffect.attrBdefense;

                    isEffect = true;
                }
                if (friendEffect.attrCdefense != 0 && Math.Abs(friendEffect.attrAdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.C]))
                {
                    target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.C] = friendEffect.attrCdefense;

                    isEffect = true;
                }
                if (friendEffect.attrDdefense != 0 && Math.Abs(friendEffect.attrAdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.D]))
                {
                    target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.D] = friendEffect.attrDdefense;

                    isEffect = true;
                }
                if (friendEffect.attrEdefense != 0 && Math.Abs(friendEffect.attrAdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.E]))
                {
                    target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.E] = friendEffect.attrEdefense;

                    isEffect = true;
                }
                if (friendEffect.attrFdefense != 0 && Math.Abs(friendEffect.attrAdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.F]))
                {
                    target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.F] = friendEffect.attrFdefense;

                    isEffect = true;
                }
                if (friendEffect.attrGdefense != 0 && Math.Abs(friendEffect.attrAdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.G]))
                {
                    target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.G] = friendEffect.attrGdefense;

                    isEffect = true;
                }
                if (friendEffect.attrHdefense != 0 && Math.Abs(friendEffect.attrAdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.H]))
                {
                    target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.H] = friendEffect.attrHdefense;

                    isEffect = true;
                }
#endif

                    if (target.HitPoint <= 0)
                    {
                        if (totalHitPointDamage > 0)
                        {
                            // ダメージ効果があった場合は戦闘不能を付与する
                            // If there is a damage effect, grant incapacity
                            target.Down(catalog, battleEvents);
                        }
                        else if (!target.IsDeadCondition())
                        {
                            // 「戦闘不能」から回復したがHPが0のままだともう一度「戦闘不能」と扱われてしまうのでHPを回復させておく
                            // Recovered from \
                            target.HitPoint = 1;
                            target.consumptionStatusValue.SetStatus(gs.maxHPStatusID, 1);
                        }
                    }

                    target.ConsistancyConsumptionStatus(gameSettings);

                    // 上限チェック
                    // upper limit check
                    if (target.HitPoint > target.MaxHitPoint) target.HitPoint = target.MaxHitPoint;
                    if (target.MagicPoint > target.MaxMagicPoint) target.MagicPoint = target.MaxMagicPoint;

                    // 下限チェック
                    // lower limit check
                    if (target.HitPoint < 0) target.HitPoint = 0;
                    if (target.MagicPoint < 0) target.MagicPoint = 0;
                    #endregion
                }

                if (isEffect)
                {
                    friendEffectCharacters.Add(target);
                }

                if (isReflection)
                    target.CommandReactionType = ReactionType.Reflection;
                else if (totalHitPointDamage > 0)
                    target.CommandReactionType = ReactionType.Damage;
                else if (isEffect)
                    target.CommandReactionType = ReactionType.Heal;
                else
                    target.CommandReactionType = ReactionType.None;
            }

            // 対象にスキル効果を反映
            // Reflect skill effect on target
            foreach (var target in enemyEffectTargets)
            {
                bool isEffect = false;
                bool isDisplayMiss = false;
                bool isReflection = false;

                var reflect = target.GetReflectionParam(skill, false);
                if (checkReflection && reflect != null && reflect.Percent > battleRandom.Next(100))
                {
                    #region 反射
                    isReflection = true;
                    reflectionInfos.Add(new ReflectionInfo(false, target, reflect));
                    #endregion
                }
                else if (skill.HitRate > battleRandom.Next(100))
				{
                    #region 敵効果発動
                    if (!enemyEffect.EffectParamSettings.UseOldEffectParam)
                    {
                        var effectValueDic = new Dictionary<Guid, int>();

                        foreach (var effectParam in enemyEffect.EffectParamSettings.GetConsumptionStatusValueChangeEffectParamList())
                        {
                            if (effectParam.ChangeParam == 0)
                            {
                                continue;
                            }

                            var info = gameSettings.GetCastStatusParamInfo(effectParam.Id);

                            if (info == null)
                            {
                                continue;
                            }

                            var value = 0;

                            switch (effectParam.ChangeType)
                            {
                                case Util.StatusChangeType.Unknwon:
                                    break;
                                case Util.StatusChangeType.Direct:
                                    value = effectParam.ChangeParam;
                                    break;
                                case Util.StatusChangeType.Percent:
                                    {
                                        var baseStatus = (effectParam.BaseStatusId != info.guId) ? effecter : target;

                                        value = baseStatus.GetSystemStatus(gameSettings, effectParam.BaseStatusId) * effectParam.ChangeParam / 100;
                                    }
                                    break;
                                default:
                                    break;
                            }

                            if (!effectValueDic.ContainsKey(info.guId))
                            {
                                effectValueDic[info.guId] = 0;
                            }

                            effectValueDic[info.guId] += value;
                        }

                        foreach (var key in effectValueDic.Keys.ToArray())
                        {
                            var effectValue = effectValueDic[key];

                            if (effectValue <= 0)
                            {
                                continue;
                            }

                            var info = gameSettings.GetCastStatusParamInfo(key);

                            if (info.Consumption)
                            {
                                // ダメージなので属性耐性を考慮
                                // Since it is damage, consider attribute resistance.
                                var baseEffectValueTmp = effectValue * target.ResistanceAttackAttributePercent(enemySkillAttribute);
                                
                                // DUAL-TYPE SYSTEM: Apply additional type effectiveness for skills
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "SKILL DAMAGE", 
                                    string.Format("🎯 SKILL DAMAGE CALC: {0} using {1} on {2}", effecter.Name, skill.name, target.Name));
                                float dualTypeMultiplier = CalculateDualTypeEffectiveness(effecter, target);
                                var effectValueTmp = baseEffectValueTmp * dualTypeMultiplier;
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "SKILL DAMAGE", 
                                    string.Format("💥 Base: {0} × Type: {1} = Final: {2}", baseEffectValueTmp, dualTypeMultiplier, effectValueTmp));

                                if (skill.CanGuard && (info.guId == gameSettings.maxHPStatusID))
                                {
                                    effectValueTmp *= target.DamageRate;
                                }

                                effectValueDic[info.guId] = (int)effectValueTmp;
                            }
                        }

                        foreach (var item in enemySkillFormulaDic)
                        {
                            var info = gameSettings.GetCastStatusParamInfo(item.Key);

                            if (info == null)
                            {
                                continue;
                            }

                            if (!effectValueDic.ContainsKey(info.guId))
                            {
                                effectValueDic[info.guId] = 0;
                            }

                            var effectValueTmp = EvalFormula(item.Value, effecter, target, enemySkillAttribute, battleRandom);

                            if (skill.CanGuard && (info.guId == gameSettings.maxHPStatusID))
                            {
                                effectValueTmp *= target.DamageRate;
                            }

                            effectValueDic[info.guId] += (int)effectValueTmp;
                        }

                        foreach (var item in effectValueDic)
                        {
                            var info = gameSettings.GetCastStatusParamInfo(item.Key);

                            if (info == null)
                            {
                                continue;
                            }

                            var effectValue = Math.Max(Math.Min(item.Value, effecter.MaxDamage), -effecter.MaxDamage);

                            BattleDamageTextInfo.TextType textType;
                            var heal = effectValue < 0;

                            if (!heal && enemySkillDrainPercentDic.ContainsKey(info.ConsumptionId))
                            {
                                effectValue = Math.Min(effectValue, target.consumptionStatusValue.GetStatus(item.Key));
                            }

                            target.consumptionStatusValue.SubStatus(item.Key, effectValue);

                            if (!enemyTotalSkillDamageDic.ContainsKey(info.ConsumptionId))
							{
                                enemyTotalSkillDamageDic[info.ConsumptionId] = 0;
                            }

                            enemyTotalSkillDamageDic[info.ConsumptionId] += effectValue;

                            switch (info.Key)
                            {
                                case Rom.GameSettings.MaxHPStatusKey:
                                    target.HitPoint -= effectValue;

                                    target.ConsistancyHPPercentConditions(catalog, battleEvents);

                                    if (!heal)
                                    {
                                        CheckDamageRecovery(target, effectValue);

                                        SetCounterAction(target, effecter);

                                        totalHitPointDamage += effectValue;
                                    }
                                    break;
                                case Rom.GameSettings.MaxMPStatusKey:
                                    target.MagicPoint -= effectValue;
                                    break;
                                default:
                                    break;
                            }

                            textType = heal ? BattleDamageTextInfo.TextType.Heal : BattleDamageTextInfo.TextType.Damage;
                            effectValue = Math.Abs(effectValue);

                            AddBattleDamageTextInfo(textInfo, textType, target, effectValue.ToString(), item.Key);

                            isEffect = true;
                        }
                    }
                    else
                    {
                        useOldEffectParam = true;

                        // HitPoint 回復 or ダメージ
                        // HitPoint recovery or damage
                        if (enemyEffect.hitpoint != 0 || enemyEffect.hitpointPercent != 0 ||
                            enemyEffect.hitpoint_powerPercent != 0 || enemyEffect.hitpoint_magicPercent != 0 ||
                            !string.IsNullOrEmpty(enemyEffect.hitpointFormula))
                        {
                            int damage = (enemyEffect.hitpoint) + (int)(enemyEffect.hitpointPercent / 100.0f * target.MaxHitPoint) + (int)(enemyEffect.hitpoint_powerPercent / 100.0f * effecter.Attack) + (int)(enemyEffect.hitpoint_magicPercent / 100.0f * effecter.Magic);

                            if (damage > effecter.MaxDamage)
                                damage = effecter.MaxDamage;
                            if (damage < -effecter.MaxDamage)
                                damage = -effecter.MaxDamage;

                            if (damage >= 0)
                            {
#if true
                                switch (target.AttackAttributeTolerance(enemyEffect.AttributeGuid))
#else
                            switch (target.AttackAttributeTolerance(enemyEffect.attribute))
#endif
                                {
                                    case AttributeToleranceType.Normal:
                                    case AttributeToleranceType.Strong:
                                    case AttributeToleranceType.Weak:
                                        {
#if true
                                            float baseEffectValue = damage * target.ResistanceAttackAttributePercent(enemyEffect.AttributeGuid) * target.DamageRate;
                                            
                                            // DUAL-TYPE SYSTEM: Apply additional type effectiveness for skills
                                            float dualTypeMultiplier = CalculateDualTypeEffectiveness(effecter, target);
                                            float effectValue = baseEffectValue * dualTypeMultiplier;
#else
                                        float effectValue = damage * target.ResistanceAttackAttributePercent(enemyEffect.attribute) * target.DamageRate;
#endif
                                            if (effectValue > effecter.MaxDamage)
                                                effectValue = effecter.MaxDamage;
                                            if (effectValue < -effecter.MaxDamage)
                                                effectValue = -effecter.MaxDamage;
                                            damage = (int)effectValue;
                                        }
                                        break;

                                    case AttributeToleranceType.Absorb:
                                        {
#if true
                                            float baseEffectValue = damage * target.ResistanceAttackAttributePercent(enemyEffect.AttributeGuid) * target.DamageRate;
                                            
                                            // DUAL-TYPE SYSTEM: Apply additional type effectiveness for skills
                                            float dualTypeMultiplier = CalculateDualTypeEffectiveness(effecter, target);
                                            float effectValue = baseEffectValue * dualTypeMultiplier;
#else
                                        float effectValue = damage * target.ResistanceAttackAttributePercent(enemyEffect.attribute) * target.DamageRate;
#endif
                                            if (effectValue > effecter.MaxDamage)
                                                effectValue = effecter.MaxDamage;
                                            if (effectValue < -effecter.MaxDamage)
                                                effectValue = -effecter.MaxDamage;
                                            damage = (int)effectValue;
                                        }
                                        break;

                                    case AttributeToleranceType.Invalid:
                                        damage = 0;
                                        break;
                                }
                            }
                            else
                            {
                                // 回復効果だったときは耐性計算を行わない
                                // When it is a recovery effect, resistance calculation is not performed
                            }

                            // 式がある？
                            // do you have a formula?
                            if (!string.IsNullOrEmpty(enemyEffect.hitpointFormula))
                            {
#if true
                                damage += (int)(EvalFormula(enemyEffect.hitpointFormula, effecter, target, enemyEffect.AttributeGuid, battleRandom) * target.DamageRate);
#else
                                damage += (int)(EvalFormula(enemyEffect.hitpointFormula, effecter, target, enemyEffect.attribute, battleRandom) * target.DamageRate);
#endif
                                if (damage > effecter.MaxDamage)
                                    damage = effecter.MaxDamage;
                                if (damage < -effecter.MaxDamage)
                                    damage = -effecter.MaxDamage;
                            }

                            BattleDamageTextInfo.TextType textType;

                            if (damage >= 0)
                            {
                                // 攻撃
                                // attack
                                if (skill.option.drain) damage = Math.Min(damage, target.HitPoint);

                                target.HitPoint -= damage;
                                target.consumptionStatusValue.SubStatus(gs.maxHPStatusID, damage);

                                CheckDamageRecovery(target, damage);

                                totalHitPointDamage += Math.Abs(damage);
                                textType = BattleDamageTextInfo.TextType.Damage;
                                SetCounterAction(target, effecter);
                            }
                            else
                            {
                                // 回復
                                // recovery
                                target.HitPoint -= damage;
                                target.consumptionStatusValue.SubStatus(gs.maxHPStatusID, damage);
                                textType = BattleDamageTextInfo.TextType.Heal;
                            }

                            AddBattleDamageTextInfo(textInfo, textType, target, Math.Abs(damage).ToString(), gs.maxHPStatusID);

                            target.ConsistancyHPPercentConditions(catalog, battleEvents);
                            isEffect = true;
                        }

                        // MagicPoint 減少
                        // MagicPoint decrease
                        if (enemyEffect.magicpoint != 0 || enemyEffect.magicpointPercent != 0)
                        {
                            int damage = (enemyEffect.magicpoint) + (int)(enemyEffect.magicpointPercent / 100.0f * target.MaxMagicPoint);

                            if (damage > effecter.MaxDamage)
                                damage = effecter.MaxDamage;
                            if (damage < -effecter.MaxDamage)
                                damage = -effecter.MaxDamage;

                            if (skill.option.drain) damage = Math.Min(damage, target.MagicPoint);

                            BattleDamageTextInfo.TextType textType;

                            if (damage >= 0)
                            {
                                // 攻撃
                                // attack
                                if (skill.option.drain) damage = Math.Min(damage, target.HitPoint);

                                target.MagicPoint -= damage;

                                var info = gameSettings.GetCastStatusParamInfo(gs.maxMPStatusID, true);

                                if (info != null)
                                {
                                    target.consumptionStatusValue.SubStatus(info.guId, damage);
                                }

                                totalMagicPointDamage += Math.Abs(damage);
                                textType = BattleDamageTextInfo.TextType.Damage;
                            }
                            else
                            {
                                // 回復
                                // recovery
                                target.MagicPoint -= damage;

                                var info = gameSettings.GetCastStatusParamInfo(gs.maxMPStatusID, true);

                                if (info != null)
                                {
                                    target.consumptionStatusValue.SubStatus(info.guId, damage);
                                }
                                textType = BattleDamageTextInfo.TextType.Heal;
                            }

                            AddBattleDamageTextInfo(textInfo, textType, target, Math.Abs(damage).ToString(), gs.maxMPStatusID);

                            isEffect = true;
                        }
                    }

                    // 状態異常
                    // Abnormal status
                    conditionRecoveryImpl(enemyEffect.RecoveryList, target, ref isEffect);
                    // 状態異常付与に失敗した時missと出す場合は dummy のかわりに isDisplayMiss を渡す
                    // Pass isDisplayMiss instead of dummy if you want to display a miss when you fail to apply a status ailment
                    bool dummy = false;
                    conditionAssignImpl(enemyEffect.AssignList, target, ref isEffect, ref dummy);

					// パラメータ変動
					// Parameter variation
					if (target.enhanceStatusValue.InitializeEnhancementEffectStatus(target.baseStatusValue.GetCalcEffectStatuses(enemyEffect.EffectParamSettings), false))
					{
                        isEffect = true;
                    }

                    var enhancement = target.enhanceStatusValue.GetSystemStatus(gameSettings, gs.maxHPStatusID);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.MaxHitPointEnhance))      // 腕力 / strength
                    {
                        target.MaxHitPointEnhance = enhancement;

                        isEffect = true;
                    }

                    enhancement = target.enhanceStatusValue.GetSystemStatus(gameSettings, gs.maxMPStatusID);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.MaxMagicPointEnhance))      // 腕力 / strength
                    {
                        target.MaxMagicPointEnhance = enhancement;

                        isEffect = true;
                    }

                    enhancement = Util.CalcStatusChangeValue(target.PowerBase, enemyEffect.power, enemyEffect.powerStatusChangeType);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.PowerEnhancement))    // 腕力 / strength
                    {
                        target.PowerEnhancement = -enhancement;

                        isEffect = true;
                    }

                    enhancement = Util.CalcStatusChangeValue(target.VitalityBase, enemyEffect.vitality, enemyEffect.vitalityStatusChangeType);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.VitalityEnhancement))   // 体力 / physical strength
                    {
                        target.VitalityEnhancement = -enhancement;

                        isEffect = true;
                    }

                    enhancement = Util.CalcStatusChangeValue(target.MagicBase, enemyEffect.magic, enemyEffect.magicStatusChangeType);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.MagicEnhancement))    // 魔力 / magical power
                    {
                        target.MagicEnhancement = -enhancement;

                        isEffect = true;
                    }

                    enhancement = Util.CalcStatusChangeValue(target.SpeedBase, enemyEffect.speed, enemyEffect.speedStatusChangeType);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.SpeedEnhancement))     // 素早さ / Agility
                    {
                        target.SpeedEnhancement = -enhancement;

                        isEffect = true;
                    }

                    enhancement = Util.CalcStatusChangeValue(target.DexterityBase, enemyEffect.dexterity, enemyEffect.dexterityStatusChangeType);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.DexterityEnhancement)) // 命中 / hit
                    {
                        target.DexterityEnhancement = -enhancement;

                        isEffect = true;
                    }

                    enhancement = Util.CalcStatusChangeValue(target.EvasionBase, enemyEffect.evasion, enemyEffect.evasionStatusChangeType);

                    if (enhancement != 0 && Math.Abs(enhancement) >= Math.Abs(target.EvasionEnhancement))   // 回避 / Avoidance
                    {
                        target.EvasionEnhancement = -enhancement;

                        isEffect = true;
                    }

                    // 各属性耐性
                    // Each attribute resistance
#if true
                    foreach (var ai in enemyEffect.AttrDefenceList)
                    {
                        if ((ai.value != 0) && (ai.attribute != Guid.Empty) &&
                            (!target.ResistanceAttackAttributeEnhance.ContainsKey(ai.attribute) ||
                            Math.Abs(ai.value) >= Math.Abs(target.ResistanceAttackAttributeEnhance[ai.attribute])))
                        {
                            target.ResistanceAttackAttributeEnhance[ai.attribute] = -ai.value;
                            isEffect = true;
                        }
                    }
                    foreach (var ci in enemyEffect.ConditionDefenceList)
                    {
                        if ((ci.value != 0) && (ci.condition != Guid.Empty) &&
                            (!target.ResistanceAilmentEnhance.ContainsKey(ci.condition) ||
                            Math.Abs(ci.value) >= Math.Abs(target.ResistanceAilmentEnhance[ci.condition])))
                        {
                            target.ResistanceAilmentEnhance[ci.condition] = -ci.value;
                            isEffect = true;
                        }
                    }
#else
                    if (enemyEffect.attrAdefense != 0 && Math.Abs(enemyEffect.attrAdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.A]))
                    {
                        target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.A] = -enemyEffect.attrAdefense;

                        isEffect = true;
                    }
                    if (enemyEffect.attrBdefense != 0 && Math.Abs(enemyEffect.attrBdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.B]))
                    {
                        target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.B] = -enemyEffect.attrBdefense;

                        isEffect = true;
                    }
                    if (enemyEffect.attrCdefense != 0 && Math.Abs(enemyEffect.attrCdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.C]))
                    {
                        target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.C] = -enemyEffect.attrCdefense;

                        isEffect = true;
                    }
                    if (enemyEffect.attrDdefense != 0 && Math.Abs(enemyEffect.attrDdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.D]))
                    {
                        target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.D] = -enemyEffect.attrDdefense;

                        isEffect = true;
                    }
                    if (enemyEffect.attrEdefense != 0 && Math.Abs(enemyEffect.attrEdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.E]))
                    {
                        target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.E] = -enemyEffect.attrEdefense;

                        isEffect = true;
                    }
                    if (enemyEffect.attrFdefense != 0 && Math.Abs(enemyEffect.attrFdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.F]))
                    {
                        target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.F] = -enemyEffect.attrFdefense;

                        isEffect = true;
                    }
                    //
                    if (enemyEffect.attrGdefense != 0 && Math.Abs(enemyEffect.attrGdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.G]))
                    {
                        target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.G] = -enemyEffect.attrGdefense;

                        isEffect = true;
                    }
                    if (enemyEffect.attrHdefense != 0 && Math.Abs(enemyEffect.attrHdefense) >= Math.Abs(target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.H]))
                    {
                        target.ResistanceAttackAttributeEnhance[(int)AttackAttributeType.H] = -enemyEffect.attrHdefense;

                        isEffect = true;
                    }
#endif

                    target.ConsistancyConsumptionStatus(gameSettings);

                    // 上限チェック
                    // upper limit check
                    if (target.HitPoint > target.MaxHitPoint) target.HitPoint = target.MaxHitPoint;
                    if (target.MagicPoint > target.MaxMagicPoint) target.MagicPoint = target.MaxMagicPoint;

                    // 下限チェック
                    // lower limit check
                    if (target.HitPoint < 0) target.HitPoint = 0;
                    if (target.MagicPoint < 0) target.MagicPoint = 0;
                    #endregion
                }
                else
                {
                    isDisplayMiss = true;
                }

                if (isEffect || isDisplayMiss)
                {
                    enemyEffectCharacters.Add(target);
                }

                if (isDisplayMiss)
                {
                    AddBattleMissTextInfo(textInfo, target);
                }

                if(isReflection)
                    target.CommandReactionType = ReactionType.Reflection;
                else if (totalHitPointDamage > 0)
                    target.CommandReactionType = ReactionType.Damage;
                else if (isEffect)
                    target.CommandReactionType = ReactionType.Heal;
                else
                    target.CommandReactionType = ReactionType.None;
            }

            // 与えたダメージ分 自分のHP, MPを回復する
            // Recover HP and MP equal to the damage dealt
            if ((friendSkillDrainPercentDic.Count > 0) || (enemySkillDrainPercentDic.Count > 0))
            {
                var totalDamageDic = new Dictionary<Guid, int>();

                CalcDrainValue(totalDamageDic, friendTotalSkillValueDic, friendSkillDrainPercentDic, -1);
                CalcDrainValue(totalDamageDic, enemyTotalSkillDamageDic, enemySkillDrainPercentDic, 1);

                foreach (var item in totalDamageDic)
                {
                    var totalDamage = item.Value;

                    if (totalDamage > 0)
                    {
                        var info = gameSettings.GetCastStatusParamInfo(item.Key);

                        if (info == null)
                        {
                            continue;
                        }

                        switch (info.Key)
                        {
                            case Rom.GameSettings.MaxHPStatusKey:
                                effecter.HitPoint += totalDamage;
                                break;
                            case Rom.GameSettings.MaxMPStatusKey:
                                effecter.MagicPoint += totalDamage;
                                break;
                            default:
                                break;
                        }

                        effecter.consumptionStatusValue.AddStatus(info.guId, totalDamage);

                        AddBattleDamageTextInfo(textInfo, BattleDamageTextInfo.TextType.Heal, effecter, totalDamage.ToString(), item.Key);
                    }
                }
            }
            else if (useOldEffectParam && skill.option.drain)
            {
                if (totalHitPointDamage > 0)
                {
                    effecter.HitPoint += totalHitPointDamage;
                    effecter.consumptionStatusValue.AddStatus(gs.maxHPStatusID, totalHitPointDamage);

                    AddBattleDamageTextInfo(textInfo, BattleDamageTextInfo.TextType.Heal, effecter, totalHitPointDamage.ToString(), gs.maxHPStatusID);
                }

                if (totalMagicPointDamage > 0)
                {
                    effecter.MagicPoint += totalMagicPointDamage;

                    var info = gameSettings.GetCastStatusParamInfo(gs.maxMPStatusID, true);

                    if (info != null)
                    {
                        effecter.consumptionStatusValue.AddStatus(info.guId, totalMagicPointDamage);
                    }

                    AddBattleDamageTextInfo(textInfo, BattleDamageTextInfo.TextType.Heal, effecter, totalMagicPointDamage.ToString(), gs.maxMPStatusID);
                }
            }

            if (option.selfDestruct)
            {
                effecter.HitPoint = 0;
                effecter.consumptionStatusValue.SetStatus(gs.maxHPStatusID, 0);
            }

            effecter.ConsistancyConsumptionStatus(gameSettings);

            // スキル使用者のパラメータ 上限チェック
            // Skill user parameter upper limit check
            if (effecter.HitPoint > effecter.MaxHitPoint) effecter.HitPoint = effecter.MaxHitPoint;
            if (effecter.MagicPoint > effecter.MaxMagicPoint) effecter.MagicPoint = effecter.MaxMagicPoint;

            if (effecter.HitPoint < 0) effecter.HitPoint = 0;
            if (effecter.MagicPoint < 0) effecter.MagicPoint = 0;

            friend = friendEffectCharacters.ToArray();
            enemy = enemyEffectCharacters.ToArray();

            // イベント開始
            // event start
            var oldData = true;
            var idx = 0;

            if ((friendEffect != null) && !friendEffect.EffectParamSettings.UseOldEffectParam && (friendEffectTargets.Length > 0))
			{
                var referenceId = friendEffect.EffectParamSettings.GetReferenceIdEffectParam(Catalog.SIGNATURE_EVENT);

				if ((referenceId != null) && (referenceId.RefId != Guid.Empty))
				{
                    if (option.waitCommon)
                    {
                        waitForCommons[idx++] = referenceId.RefId;
                    }

                    // 効果がコモン発動だけだった場合、ターゲットリストはそのままにする
                    // If the effect is only a common activation, leave the target list as is.
                    if (friendEffect.EffectParamSettings.EffectParamList.Count == 1)
                        friend = friendEffectTargets;

                    if (checkReflection)
                        battleEvents.start(referenceId.RefId);
                }

                oldData = false;
            }

            if ((enemyEffect != null) && !enemyEffect.EffectParamSettings.UseOldEffectParam && (enemyEffectTargets.Length > 0))
            {
                var referenceId = enemyEffect.EffectParamSettings.GetReferenceIdEffectParam(Catalog.SIGNATURE_EVENT);

                if ((referenceId != null) && (referenceId.RefId != Guid.Empty))
                {
                    if (option.waitCommon)
                    {
                        waitForCommons[idx++] = referenceId.RefId;
                    }

                    // 効果がコモン発動だけだった場合、ターゲットリストはそのままにする
                    // If the effect is only a common activation, leave the target list as is.
                    if (enemyEffect.EffectParamSettings.EffectParamList.Count == 1)
                        enemy = enemyEffectTargets;

                    if (checkReflection)
                        battleEvents.start(referenceId.RefId);
                }

                oldData = false;
            }

            if (oldData && (option.commonExec != Guid.Empty))
            {
                if (option.waitCommon)
                    waitForCommon = option.commonExec;

                if (checkReflection)
                    battleEvents.start(option.commonExec);
            }

            battleEvents.setLastSkillUserIndex(effecter);
            reflections = reflectionInfos.ToArray();
        }

        private void CalcDrainValue(Dictionary<AttackAttributeType, int> inTotalDamageDic, Dictionary<AttackAttributeType, int> inTotalSkillValueDic, Dictionary<Guid, Rom.DamageDrainEffectParam> inSkillDrainPercentDic, int inMulFactor)
		{
            foreach (var item in inTotalSkillValueDic)
            {
				if (inSkillDrainPercentDic.ContainsKey(item.Key))
				{
                    var skillDrain = inSkillDrainPercentDic[item.Key];

                    if (!inTotalDamageDic.ContainsKey(skillDrain.RecoveryId))
                    {
                        inTotalDamageDic[skillDrain.RecoveryId] = 0;
                    }

                    inTotalDamageDic[skillDrain.RecoveryId] += inMulFactor * (int)(item.Value * skillDrain.Percent / 100);
                }
            }
        }

        public override void FixedUpdate()
        {
            (battleViewer as BattleViewer3D)?.FixedUpdate();
        }

        private void conditionAssignImpl(List<Rom.ConditionInfo> list, BattleCharacterBase target, ref bool isEffect, ref bool isDisplayMiss)
        {
            foreach (var info in list)
            {
                if (info.value != 0)
                {
                    var condition = catalog.getItemFromGuid<Rom.Condition>(info.condition);
                    var percent = target.GetResistanceAilmentStatus(info.condition);

                    if (condition == null)
                        continue;

                    if (target.SetCondition(catalog, info.condition, battleEvents, true))
                    {
                        if (condition != null)
                        {
                            if ((target.selectedBattleCommandType != BattleCommandType.Skip))
                            {
                                if (target != activeCharacter &&
                                    (condition.IsActionDisabled || condition.IsAutoAttack))
                                {
                                    target.selectedBattleCommandType = BattleCommandType.Cancel;
                                }

                                if (condition.IsDeadCondition)
                                {
                                    target.HitPoint = 0;
                                    target.consumptionStatusValue.SetStatus(catalog.getGameSettings().maxHPStatusID, 0);
                                }
                            }
                        }

                        // メッセージ用変数が未生成であれば作る
                        // If the message variable is not generated, create it
                        if (!displayedSetConditionsDic.ContainsKey(target))
                        {
                            displayedSetConditionsDic.Add(target, new List<Rom.Condition>());
                        }

                        // メッセージ用に現在の状態異常をセットする
                        // set current status for message
                        if (target.conditionInfoDic.ContainsKey(info.condition))
                        {
                            condition = catalog.getItemFromGuid<Rom.Condition>(info.condition);
                            displayedSetConditionsDic[target].Add(condition);
                        }

                        isEffect = true;
                    }
                    else
                    {
                        isDisplayMiss = true;
                    }
                }
            }
        }

        private void conditionRecoveryImpl(List<Rom.ConditionInfo> list, BattleCharacterBase target, ref bool isEffect)
        {
            foreach (var info in list)
            {
                if ((info.value != 0) && target.conditionInfoDic.ContainsKey(info.condition))
                {
                    target.RecoveryCondition(info.condition, battleEvents, Rom.Condition.RecoveryType.Normal);

                    var condition = catalog.getItemFromGuid<Rom.Condition>(info.condition);

                    if (condition != null)
                    {
                        recoveryStatusInfo.Add(new RecoveryStatusInfo(target, condition));

                        if (condition.IsActionDisabled || condition.IsAutoAttack)
                        {
                            target.selectedBattleCommandType = BattleCommandType.Undecided;
                        }

                        if (condition.IsDeadCondition)
                        {
                            // 戦闘不能回復効果で回復がゼロポイントの場合は強制的に1回復する
                            // If the recovery is 0 points due to the incapacity recovery effect, it will be forcibly recovered by 1.
                            if (target.HitPoint == 0)
                            {
                                target.HitPoint = 1;
                                target.consumptionStatusValue.SetStatus(catalog.getGameSettings().maxHPStatusID, 1);
                            }

                            var targetEnemy = target as BattleEnemyData;

                            if (targetEnemy != null)
                            {
                                battleViewer.AddFadeInCharacter(targetEnemy);
                            }
                        }
                    }

                    target.ConsistancyHPPercentConditions(catalog, battleEvents);
                    isEffect = true;
                }
            }
        }

        private void PaySkillCost(BattleCharacterBase effecter, Rom.NSkill skill)
        {
            // スキル発動時のコストとして発動者の消費ステータスを消費
            // Consumes the caster's consumption status as the skill activation cost.
            PaySkillCostStatus(effecter, skill);

            // 味方だったらアイテムを消費
            // Consumes items if it is an ally
            if (effecter is BattlePlayerData)
                party.AddItem(skill.option.consumptionItem, -skill.option.consumptionItemAmount);
        }

        private bool UseItem(Rom.NItem item, BattleCharacterBase target, List<BattleDamageTextInfo> textInfo, List<RecoveryStatusInfo> recoveryStatusInfo)
        {
            var gs = catalog.getGameSettings();
            var expendable = item.expendable;
            bool isUsedItem = false;
            var isDeadCondition = target.IsDeadCondition();

            {
                if (isDeadCondition)
                {
                    foreach (var info in expendable.RecoveryList)
                    {
                        if (info.value != 0)
                        {
                            var condition = catalog.getItemFromGuid<Rom.Condition>(info.condition);

                            if (isDeadCondition && condition != null)
                            {
                                if (condition.IsDeadCondition)
                                {
                                    // 蘇生アイテムなら、生きているものとする
                                    // If it's a resurrection item, it's considered alive.
                                    isDeadCondition = false;

                                    break;
                                }
                            }
                        }
					}
				}

                // ステータス 強化
                // status enhancement
                if (!isDeadCondition && target.enhanceStatusValue.InitializeEnhancementEffectStatus(target.baseStatusValue.GetCalcEffectStatuses(item.EffectParamSettings), true))
                {
                    isUsedItem = true;
                }

                // 最大HP 増加
                // Max HP increase
                if (expendable.maxHitpoint > 0 && Math.Abs(expendable.maxHitpoint) >= Math.Abs(target.MaxHitPointEnhance) && !isDeadCondition)
                {
                    target.MaxHitPointEnhance = expendable.maxHitpoint;
                    isUsedItem = true;
                }

                // 最大MP 増加
                // Max MP increase
                if (expendable.maxMagitpoint > 0 && Math.Abs(expendable.maxMagitpoint) >= Math.Abs(target.MaxMagicPointEnhance) && !isDeadCondition)
                {
                    target.MaxMagicPointEnhance = expendable.maxMagitpoint;
                    isUsedItem = true;
                }

                if (!item.EffectParamSettings.UseOldEffectParam)
                {
                    if (!isDeadCondition)
                    {
                        var healFormulaDic = new Dictionary<Guid, string>();

                        foreach (var effectParam in item.EffectParamSettings.GetEtcEffectParamList())
                        {
                            switch (effectParam.Type)
                            {
                                case Rom.EffectParamBase.EffectType.ConsumptionValueFormula:
                                    if ((effectParam is Rom.ConsumptionValueFormulaEffectParam healFormula) && (healFormula.FormulaType == Rom.ConsumptionValueFormulaEffectParam.FormulaEffectType.Heal) && !string.IsNullOrEmpty(healFormula.Formula))
                                    {
                                        healFormulaDic[healFormula.TargetId] = healFormula.Formula;
                                    }
                                    break;
                                default:
                                    break;
                            }
                        }

                        if (Heal(target, item.EffectParamSettings, false, healFormulaDic, textInfo, Guid.Empty))
                        {
                            isUsedItem = true;
                        }

                        target.ConsistancyHPPercentConditions(catalog, battleEvents);
                    }
                }
                else
                {
                    // 回復
                    // recovery
                    int healHitPoint = 0;
                    int healMagicPoint = 0;

                    // HP回復 (固定値)
                    // HP recovery (fixed value)
                    if (expendable.hitpoint > 0 && !isDeadCondition)
                    {
                        healHitPoint += expendable.hitpoint;
                        isUsedItem = true;
                    }

                    // HP回復 (割合)
                    // HP recovery (percentage)
                    if (expendable.hitpointPercent > 0 && !isDeadCondition)
                    {
                        healHitPoint += (int)(expendable.hitpointPercent / 100.0f * target.MaxHitPoint);
                        isUsedItem = true;
                    }

                    // MP回復 (固定値)
                    // MP recovery (fixed value)
                    if (expendable.magicpoint > 0 && !isDeadCondition)
                    {
                        healMagicPoint += expendable.magicpoint;
                        isUsedItem = true;
                    }

                    // MP回復 (割合)
                    // MP recovery (percentage)
                    if (expendable.magicpointPercent > 0 && !isDeadCondition)
                    {
                        healMagicPoint += (int)(expendable.magicpointPercent / 100.0f * target.MaxMagicPoint);
                        isUsedItem = true;
                    }

                    if (healHitPoint > 0)
                    {
                        target.HitPoint += healHitPoint;
                        target.consumptionStatusValue.AddStatus(gs.maxHPStatusID, healHitPoint);

                        AddBattleDamageTextInfo(textInfo, BattleDamageTextInfo.TextType.Heal, target, healHitPoint.ToString(), gs.maxHPStatusID);
                    }

                    if (healMagicPoint > 0)
                    {
                        target.MagicPoint += healMagicPoint;

                        var info = gameSettings.GetCastStatusParamInfo(gs.maxMPStatusID, true);

                        if (info != null)
                        {
                            target.consumptionStatusValue.AddStatus(info.guId, healMagicPoint);
                        }

                        AddBattleDamageTextInfo(textInfo, BattleDamageTextInfo.TextType.Heal, target, healMagicPoint.ToString(), gs.maxMPStatusID);
                    }

                    target.ConsistancyConsumptionStatus(gameSettings);
                    target.HitPoint = Math.Min(target.HitPoint, target.MaxHitPoint);
                    target.ConsistancyHPPercentConditions(catalog, battleEvents);
                    target.MagicPoint = Math.Min(target.MagicPoint, target.MaxMagicPoint);
                }

                target.enhanceStatusValue.InitializeEnhancementEffectStatus(target.baseStatusValue.GetCalcEffectStatuses(item.EffectParamSettings), true);

                if (expendable.power > 0 && Math.Abs(expendable.power) >= Math.Abs(target.PowerEnhancement) && !isDeadCondition)
                {
                    target.PowerEnhancement = expendable.power;
                    isUsedItem = true;
                }

                if (expendable.vitality > 0 && Math.Abs(expendable.vitality) >= Math.Abs(target.VitalityEnhancement) && !isDeadCondition)
                {
                    target.VitalityEnhancement = expendable.vitality;
                    isUsedItem = true;
                }
                if (expendable.magic > 0 && Math.Abs(expendable.magic) > Math.Abs(target.MagicEnhancement) && !isDeadCondition)
                {
                    target.MagicEnhancement = expendable.magic;
                    isUsedItem = true;
                }
                if (expendable.speed > 0 && Math.Abs(expendable.speed) >= Math.Abs(target.SpeedEnhancement) && !isDeadCondition)
                {
                    target.SpeedEnhancement = expendable.speed;
                    isUsedItem = true;
                }

                // 状態異常回復
                // status ailment recovery
                foreach (var info in expendable.RecoveryList)
                {
                    if ((info.value != 0) && target.conditionInfoDic.ContainsKey(info.condition))
                    {
                        target.RecoveryCondition(info.condition, battleEvents, Rom.Condition.RecoveryType.Normal);

                        var condition = catalog.getItemFromGuid<Rom.Condition>(info.condition);

                        if (condition != null)
                        {
                            recoveryStatusInfo.Add(new RecoveryStatusInfo(target, condition));

                            if (condition.IsActionDisabled || condition.IsAutoAttack)
                            {
                                target.selectedBattleCommandType = BattleCommandType.Undecided;
                            }

                            if (condition.IsDeadCondition)
                            {
                                // 戦闘不能 回復
                                // incapable of fighting recovery
                                // アイテム使用時の状況で想定されるケース
                                // Assumed cases when using items
                                // ケース1 : 対象となるキャラクターが戦闘不能時 => そのままアイテムを使用して戦闘可能状態に回復 (実装OK)
                                // Case 1 : When the target character is unable to fight =\u003e Use the item as it is to recover to a fighting state (implementation OK)
                                // ケース2 : 対象となるキャラクターが戦闘可能状態だがパーティ内に戦闘不能状態のキャラクターがいる => 対象を変更してアイテムを使用
                                // Case 2 : The target character is ready to fight, but there is a character in the party who is unable to fight =\u003e Change the target and use the item
                                // ケース3 : パーティ内に戦闘不能状態のキャラクターが1人もいない => アイテムを使用しない
                                // Case 3 : No characters in the party are incapacitated =\u003e Do not use items
                                var targetEnemy = target as BattleEnemyData;

                                if (targetEnemy != null)
                                {
                                    battleViewer.AddFadeInCharacter(targetEnemy);
                                }

                                int recoveryHitPoint = 0;

                                recoveryHitPoint += expendable.hitpoint;
                                recoveryHitPoint += (int)(expendable.hitpointPercent / 100.0f * target.MaxHitPoint);

                                if (recoveryHitPoint <= 0)
                                {
                                    recoveryHitPoint = 1;

                                    AddBattleDamageTextInfo(textInfo, BattleDamageTextInfo.TextType.Heal, target, recoveryHitPoint.ToString(), gs.maxHPStatusID);

                                    recoveryHitPoint = Math.Min(recoveryHitPoint, target.MaxHitPoint);

                                    target.HitPoint = recoveryHitPoint;
                                    target.consumptionStatusValue.SetStatus(gs.maxHPStatusID, recoveryHitPoint);
                                }
                            }
                        }

                        target.ConsistancyHPPercentConditions(catalog, battleEvents);
                        isUsedItem = true;
                    }
                }

                bool isEffect = false;
                bool isDisplayMiss = false;

                conditionAssignImpl(item.EffectParamSettings.GetAttachConditionList(), target, ref isEffect, ref isDisplayMiss);

                if (isDisplayMiss)
                {
                    // 状態異常付与に失敗した時missと出す場合はコメントアウトを外す
                    // Remove the comment out if you want to output a miss when you fail to apply the status ailment
                    //AddBattleMissTextInfo(textInfo, target);
                }
            }

            // イベント開始
            // event start
            if (expendable.commonExec != Guid.Empty)
            {
                battleEvents.start(expendable.commonExec);
                isUsedItem = true;
            }

            return isUsedItem;
        }

        private void UpdateEnhanceEffect(List<EnhanceEffect> enhanceEffects)
        {
            foreach (var effect in enhanceEffects)
            {
                effect.turnCount++;
                effect.enhanceEffect = (int)(effect.enhanceEffect * effect.diff);
            }

            // 終了条件を満たした効果を無効にする
            // Disable effects that meet the end condition
            enhanceEffects.RemoveAll(effect => effect.type == EnhanceEffect.EnhanceEffectType.TurnEffect && effect.turnCount >= effect.durationTurn);
            enhanceEffects.RemoveAll(effect => effect.type == EnhanceEffect.EnhanceEffectType.DurationEffect && effect.enhanceEffect <= 0);
        }

        private void CheckBattleCharacterDown()
        {
            // Collect player switches to perform after enumeration
            var playerSwitches = new List<(BattlePlayerData outgoing, BattlePlayerData incoming)>();
            
            foreach (var player in playerData)
            {
                player.ConsistancyHPPercentConditions(catalog, battleEvents);

                if (player.HitPoint <= 0)
                {
                    // 戦闘不能時は実行予定のコマンドをキャンセルする
                    // Cancels scheduled commands when incapacitated
                    // 同一ターン内で蘇生しても行動できない仕様でOK
                    // It is OK with specifications that can not act even if revived in the same turn
                    player.selectedBattleCommandType = BattleCommandType.Nothing_Down;

                    player.ChangeEmotion(Resource.Face.FaceType.FACE_SORROW);

                    player.Down(catalog, battleEvents);
                    
                    // Pokemon-style: Collect automatic switch to perform after enumeration
                    if (stockPlayerData.Count > 0 && stockPlayerData.Any(p => p.HitPoint > 0))
                    {
                        var nextPokemon = stockPlayerData.FirstOrDefault(p => p.HitPoint > 0);
                        if (nextPokemon != null)
                        {
                            playerSwitches.Add((player, nextPokemon));
                        }
                    }
                }

                SetBattleStatusData(player);
            }
            
            // Perform player switches after enumeration is complete
            foreach (var (outgoing, incoming) in playerSwitches)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "AutoSwitch", 
                    string.Format("{0} fainted! Automatically sending out {1}", outgoing.Name, incoming.Name));
                
                PerformPlayerAutoSwitch(outgoing, incoming);
            }

            // Collect enemy switches to perform after enumeration
            var enemySwitches = new List<(BattleEnemyData outgoing, BattleEnemyData incoming)>();

            foreach (var enemy in enemyData)
            {
                enemy.ConsistancyHPPercentConditions(catalog, battleEvents);

                // DEBUG: Log enemy HP status
                if (enemy.HitPoint <= 10) // Log when enemy is low on HP
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "EV Debug", 
                        string.Format("Enemy {0} HP: {1}", enemy.Name, enemy.HitPoint));
                }

                if (enemy.HitPoint <= 0)
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "EV Debug", 
                        string.Format("Enemy {0} died! Calling ProcessEVGains...", enemy.Name));
                    
                    // EV SYSTEM: Process EV gains from defeated enemy
                    ProcessEVGains(enemy);
                    
                    enemy.Down(catalog, battleEvents);
                    enemy.selectedBattleCommandType = BattleCommandType.Nothing_Down;
                    
                    // Pokemon-style: Check for auto-switch when enemy dies if reserves available
                    if (stockEnemyData.Count > 0 && stockEnemyData.Any(e => e.HitPoint > 0))
                    {
                        var switchTarget = stockEnemyData.FirstOrDefault(e => e.HitPoint > 0);
                        if (switchTarget != null)
                        {
                            enemySwitches.Add((enemy, switchTarget));
                        }
                    }
                }
            }
            
            // Perform enemy switches after enumeration is complete
            foreach (var (outgoing, incoming) in enemySwitches)
            {
                PerformEnemyAutoSwitch(outgoing, incoming);
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, outgoing.Name,
                    string.Format("Enemy automatically switches to {0}", incoming.Name));
            }

            foreach (var player in playerData)
            {
                battleViewer.SetPlayerStatusEffect(player);
            }

            bool isPlaySE = false;

            foreach (var enemyMonster in enemyData)
            {
                if ((enemyMonster.HitPoint <= 0 || enemyMonster.IsDeadCondition()) && battleViewer.IsEndMotion(enemyMonster))
                {
                    if (!battleViewer.ContainsFadeOutCharacter(enemyMonster) && enemyMonster.imageAlpha > 0)
                    {
                        enemyMonster.HitPoint = 0;
                        enemyMonster.consumptionStatusValue.SetStatus(catalog.getGameSettings().maxHPStatusID, 0);

                        enemyMonster.selectedBattleCommandType = BattleCommandType.Cancel;

                        // フェードアウト開始を遅らせる
                        // Delay start of fadeout
                        enemyMonster.imageAlpha = 1 + BattleViewer.FADEINOUT_SPEED * 20;
                        battleViewer.AddFadeOutCharacter(enemyMonster);

                        isPlaySE = true;
                    }
                }
                else
                {
                    if (enemyMonster.imageAlpha < 0)
                    {
                        // ここだとタイミングが遅くなるので、状態異常を付与する段階で行う事にする
                        // Since the timing will be delayed here, I decided to do it at the stage of granting status ailments.
                        //battleViewer.AddFadeInCharacter(enemyMonster);
                    }
                }

            }

            if (isPlaySE)
            {
                Audio.PlaySound(owner.se.defeat);
            }

        }
        private void SetBattleStatusData(BattleCharacterBase player, bool useConsumptionStatusValueTweener = false)
        {
            player.battleStatusData.statusValue.InitializeStatus(player.baseStatusValue);

			// 消費値の変化を補間させるか？
			// Do you interpolate changes in consumption values?
			if (useConsumptionStatusValueTweener)
            {
				player.nextStatusData.consumptionStatusValue.InitializeStatus(player.consumptionStatusValue);
			}
			else
            {
				player.battleStatusData.consumptionStatusValue.InitializeStatus(player.consumptionStatusValue);
				player.battleStatusData.HitPoint = player.HitPoint;
				player.battleStatusData.MagicPoint = player.MagicPoint;
			}

			player.battleStatusData.Name = player.Name;
            player.battleStatusData.MaxHitPoint = player.MaxHitPoint;
            player.battleStatusData.MaxMagicPoint = player.MaxMagicPoint;

            player.battleStatusData.ParameterStatus = BattleStatusWindowDrawer.StatusIconType.None;

            if (player.IsPowerEnhancementUp)
            {
                player.battleStatusData.ParameterStatus |= BattleStatusWindowDrawer.StatusIconType.PowerUp;
            }
            else if (player.IsPowerEnhancementDown)
            {
                player.battleStatusData.ParameterStatus |= BattleStatusWindowDrawer.StatusIconType.PowerDown;
            }

            if (player.IsVitalityEnhancementUp)
            {
                player.battleStatusData.ParameterStatus |= BattleStatusWindowDrawer.StatusIconType.VitalityUp;
            }
            else if (player.IsVitalityEnhancementDown)
            {
                player.battleStatusData.ParameterStatus |= BattleStatusWindowDrawer.StatusIconType.VitalityDown;
            }

            if (player.IsMagicEnhancementUp)
            {
                player.battleStatusData.ParameterStatus |= BattleStatusWindowDrawer.StatusIconType.MagicUp;
            }
            else if (player.IsMagicEnhancementDown)
            {
                player.battleStatusData.ParameterStatus |= BattleStatusWindowDrawer.StatusIconType.MagicDown;
            }

            if (player.IsSpeedEnhancementUp)
            {
                player.battleStatusData.ParameterStatus |= BattleStatusWindowDrawer.StatusIconType.SpeedUp;
            }
            else if (player.IsSpeedEnhancementDown)
            {
                player.battleStatusData.ParameterStatus |= BattleStatusWindowDrawer.StatusIconType.SpeedDown;
            }
        }

        internal void SetNextBattleStatus(BattleCharacterBase player)
        {
            if (player == null)
                return;

            player.startStatusData.statusValue.InitializeStatus(player.battleStatusData.statusValue);
            player.startStatusData.consumptionStatusValue.InitializeStatus(player.battleStatusData.consumptionStatusValue);

            player.startStatusData.HitPoint = player.battleStatusData.HitPoint;
            player.startStatusData.MagicPoint = player.battleStatusData.MagicPoint;

            player.startStatusData.MaxHitPoint = player.battleStatusData.MaxHitPoint;
            player.startStatusData.MaxMagicPoint = player.battleStatusData.MaxMagicPoint;

            player.nextStatusData.statusValue.InitializeStatus(player.baseStatusValue);
            player.nextStatusData.consumptionStatusValue.InitializeStatus(player.consumptionStatusValue);

            player.nextStatusData.HitPoint = player.HitPoint;
            player.nextStatusData.MagicPoint = player.MagicPoint;

            player.nextStatusData.MaxHitPoint = player.MaxHitPoint;
            player.nextStatusData.MaxMagicPoint = player.MaxMagicPoint;
        }

        internal bool UpdateBattleStatusData(BattleCharacterBase player)
        {
            bool isUpdated = false;
            var nowConsumptionStatusValue = player.battleStatusData.consumptionStatusValue;
            var startConsumptionStatusValue = player.startStatusData.consumptionStatusValue;
            var nextConsumptionStatusValue = player.nextStatusData.consumptionStatusValue;

            foreach (var info in catalog.getGameSettings().CastStatusParamInfoList)
			{
                if (info.Consumption)
                {
                    var now = nowConsumptionStatusValue.GetStatus(info.guId);
                    var start = startConsumptionStatusValue.GetStatus(info.guId);
                    var next = nextConsumptionStatusValue.GetStatus(info.guId);

					if (now != next)
					{
                        nowConsumptionStatusValue.SetStatus(info.guId, (int)((next - start) * statusUpdateTweener.CurrentValue + start));

                        isUpdated = true;
                    }
                }
            }

            if (player.battleStatusData.HitPoint != player.nextStatusData.HitPoint)
            {
                player.battleStatusData.HitPoint = (int)((player.nextStatusData.HitPoint - player.startStatusData.HitPoint) * statusUpdateTweener.CurrentValue + player.startStatusData.HitPoint);

                isUpdated = true;
            }

            if (player.battleStatusData.MagicPoint != player.nextStatusData.MagicPoint)
            {
                player.battleStatusData.MagicPoint = (int)((player.nextStatusData.MagicPoint - player.startStatusData.MagicPoint) * statusUpdateTweener.CurrentValue + player.startStatusData.MagicPoint);

                isUpdated = true;
            }

            return isUpdated;
        }

        public override void Update()
        {
            if (battleState == BattleState.SelectPlayerBattleCommand ||
                battleState == BattleState.Result ||
                battleState == BattleState.FinishFadeIn ||
                battleState == BattleState.FinishFadeOut ||
                (battleEvents?.isBusy() ?? false))
                GameMain.setGameSpeed(1);
            else
                GameMain.setGameSpeed(owner.debugSettings.battleFastForward ? 4 : battleSpeed);

            battleStateFrameCount += GameMain.getRelativeParam60FPS();

            battleEvents?.update();

            UpdateCommandSelect();

            UpdateMoveTargetPos();

            UpdateBattleState();

            if (playerData != null)
            {
                foreach (var player in playerData)
                {
                    player.Update();
                }



                foreach (var enemyMonster in enemyData)
                {
                    enemyMonster.Update();
                }



                battleViewer.Update(playerViewData, enemyMonsterViewData);
            }
        }

        /// <summary>
        /// 射程用の位置情報の更新
        /// Location update for range
        /// </summary>
        private void UpdateMoveTargetPos()
        {
            for (int i = 0; i < targetPlayerData?.Count; i++)
			{
                var player = targetPlayerData[i] as BattlePlayerData;
                var actor = Viewer.searchFromActors(player);

				if (actor != null)
				{
                    var pos = actor.getRangePos();
                    player.moveTargetPos.X = pos.X;
                    player.moveTargetPos.Z = pos.Z;
                }
            }

            for (int i = 0; i < targetEnemyData?.Count; i++)
			{
                var enemy = targetEnemyData[i] as BattleEnemyData;
                var actor = Viewer.searchFromActors(enemy);

                if (actor != null)
                {
                    var pos = actor.getRangePos();
                    enemy.moveTargetPos.X = pos.X;
                    enemy.moveTargetPos.Z = pos.Z;
                }
            }
        }

        private void UpdateBattleState()
        {
            switch (battleState)
            {
                case BattleState.StartFlash:
                    UpdateBattleState_StartFlash();
                    break;
                case BattleState.WaitFlash:
                    UpdateBattleState_WaitFlash();
                    break;
                case BattleState.StartFadeOut:
                    UpdateBattleState_StartFadeOut();
                    break;
                case BattleState.StartFadeIn:
                    UpdateBattleState_StartFadeIn();
                    break;
                case BattleState.BattleSetting:
                    UpdateBattleState_BattleSetting();
                    break;
                case BattleState.BattleStart:
                    UpdateBattleState_BattleStart();
                    break;
                case BattleState.Wait:
                    UpdateBattleState_Wait();
                    break;
                case BattleState.LockInEquipment:
                    UpdateBattleState_LockInEquipment();
                    break;
                case BattleState.PlayerTurnStart:
                    UpdateBattleState_PlayerTurnStart();
                    break;
                case BattleState.CheckTurnRecoveryStatus:
                    UpdateBattleState_CheckTurnRecoveryStatus();
                    break;
                case BattleState.DisplayTurnRecoveryStatus:
                    UpdateBattleState_DisplayTurnRecoveryStatus();
                    break;
				case BattleState.CheckBattleCharacterDown3:
					UpdateBattleState_CheckBattleCharacterDown2(BattleState.FadeMonsterImage3);
					break;
				case BattleState.FadeMonsterImage3:
					UpdateBattleState_FadeMonsterImage1(BattleState.BattleFinishCheck3);
					break;
				case BattleState.BattleFinishCheck3:
					UpdateBattleState_BattleFinishCheck1((commandSelectPlayer == null) ? BattleState.SetEnemyBattleCommand : BattleState.CheckCommandSelect);
					break;
				case WAIT_CTB_GAUGE:
                    UpdateBattleState_WaitCtbGauge();
                    break;
				case BattleState.SetEnemyBattleCommand:
                    UpdateBattleState_SetEnemyBattleCommand();
                    break;
                case BattleState.SelectActivePlayer:
                    UpdateBattleState_SelectActivePlayer();
                    break;
                case BattleState.CheckCommandSelect:
                    UpdateBattleState_CheckCommandSelect();
                    break;
                case BattleState.SetPlayerBattleCommand:
                    UpdateBattleState_SetPlayerBattleCommand();
                    break;
                case BattleState.WaitEventsBeforeCommandSelect:
                    UpdateBattleState_WaitEventsBeforeCommandSelect();
                    break;
                case BattleState.SelectPlayerBattleCommand:
                    UpdateBattleState_SelectPlayerBattleCommand();
                    break;
                case BattleState.SetPlayerBattleCommandTarget:
                    UpdateBattleState_SetPlayerBattleCommandTarget();
                    break;
                case BattleState.SortBattleActions:
                    UpdateBattleState_SortBattleActions();
                    break;
                case BattleState.ReadyExecuteCommand:
                    UpdateBattleState_ReadyExecuteCommand();
                    break;
                case BattleState.SetStatusMessageText:
                    UpdateBattleState_SetStatusMessageText();
                    break;
                case BattleState.DisplayStatusMessage:
                    UpdateBattleState_DisplayStatusMessage();
                    break;
                case BattleState.SetCommandMessageText:
                    UpdateBattleState_SetCommandMessageText();
                    break;
                case BattleState.DisplayMessageText:
                    UpdateBattleState_DisplayMessageText();
                    break;
                case BattleState.ExecuteBattleCommand:
                    UpdateBattleState_ExecuteBattleCommand();
                    break;
                case BattleState.ExecuteReflection:
                    UpdateBattleState_ExecuteReflection();
                    break;
                case BattleState.SetCommandEffect:
                    UpdateBattleState_SetCommandEffect();
                    break;
                case BattleState.SetReflectionEffect:
                    UpdateBattleState_SetReflectionEffect();
                    break;
                case BattleState.DisplayCommandEffect:
                    UpdateBattleState_DisplayCommandEffect();
                    break;
                case BattleState.DisplayDamageText:
                    UpdateBattleState_DisplayDamageText();
                    break;
                case BattleState.SetConditionMessageText:
                    UpdateBattleState_SetConditionMessageText();
                    break;
                case BattleState.DisplayConditionMessageText:
                    UpdateBattleState_DisplayConditionMessageText();
                    break;
                case BattleState.CheckCommandRecoveryStatus:
                    UpdateBattleState_CheckCommandRecoveryStatus();
                    break;
                case BattleState.DisplayCommandRecoveryStatus:
                    UpdateBattleState_DisplayCommandRecoveryStatus();
                    break;
                case BattleState.CheckBattleCharacterDown1:
                    UpdateBattleState_CheckBattleCharacterDown1();
                    break;
                case BattleState.FadeMonsterImage1:
                    UpdateBattleState_FadeMonsterImage1(BattleState.BattleFinishCheck1);
                    break;
                case BattleState.BattleFinishCheck1:
                    UpdateBattleState_BattleFinishCheck1(BattleState.ProcessPoisonStatus);
                    break;
                case BattleState.ProcessPoisonStatus:
                    UpdateBattleState_ProcessPoisonStatus();
                    break;
                case BattleState.DisplayStatusDamage:
                    UpdateBattleState_DisplayStatusDamage();
                    break;
                case BattleState.CheckBattleCharacterDown2:
                    UpdateBattleState_CheckBattleCharacterDown2(BattleState.FadeMonsterImage2);
                    break;
                case BattleState.FadeMonsterImage2:
                    UpdateBattleState_FadeMonsterImage1(BattleState.BattleFinishCheck2);
                    break;
                case BattleState.BattleFinishCheck2:
                    UpdateBattleState_BattleFinishCheck2();
                    break;
                case BattleState.StartBattleFinishEvent:
                    UpdateBattleState_StartBattleFinishEvent();
                    break;
                case BattleState.ProcessBattleFinish:
                    UpdateBattleState_ProcessBattleFinish();
                    break;
                case BattleState.ResultInit:
                    UpdateBattleState_ResultInit();
                    break;
                case BattleState.Result:
                    UpdateBattleState_Result();
                    break;
                case BattleState.PlayerChallengeEscape:
                    UpdateBattleState_PlayerChallengeEscape();
                    break;
                case BattleState.PlayerEscapeSuccess:
                    UpdateBattleState_PlayerEscapeSuccess();
                    break;
                case BattleState.StopByEvent:
                    UpdateBattleState_StopByEvent();
                    break;
                case BattleState.PlayerEscapeFail:
                    UpdateBattleState_PlayerEscapeFail();
                    break;
                case BattleState.MonsterEscape:
                    UpdateBattleState_MonsterEscape();
                    break;
                case BattleState.SetFinishEffect:
                    UpdateBattleState_SetFinishEffect();
                    break;
                case BattleState.FinishFadeOut:
                    UpdateBattleState_FinishFadeOut();
                    break;
                case BattleState.FinishFadeIn:
                    UpdateBattleState_FinishFadeIn();
                    break;
            }
        }

        private void UpdateBattleState_StartFlash()
        {
            // #14971 トランジションで画面を止めるよう修正
            // #14971 Fixed to stop screen at transition
            if (!TransitionUtil.instance.Captured)
            {
                return;
            }

            var guid = owner.data.system.transitionBattleEnter.HasValue ? owner.data.system.transitionBattleEnter.Value : gameSettings.transitionBattleEnter;
            if (catalog.getItemFromGuid(guid) == null)
                fadeScreenColorTweener.Begin(new Color(Color.Gray, 0), new Color(Color.Black, 0), 10);

            ChangeBattleState(BattleState.WaitFlash);
        }

        private void UpdateBattleState_WaitFlash()
        {
            fadeScreenColorTweener.Update();

            if (!fadeScreenColorTweener.IsPlayTween)
            {
                var guid = owner.data.system.transitionBattleEnter.HasValue ? owner.data.system.transitionBattleEnter.Value : gameSettings.transitionBattleEnter;
                if (catalog.getItemFromGuid(guid) == null)
                    fadeScreenColorTweener.Begin(new Color(Color.Black, 0), new Color(Color.Black, 255), 30);
                else
                    owner.mapScene.SetWipe(guid);
                ChangeBattleState(BattleState.StartFadeOut);
            }
        }

        private void UpdateBattleState_StartFadeOut()
        {
            var guid = owner.data.system.transitionBattleEnter.HasValue ? owner.data.system.transitionBattleEnter.Value : gameSettings.transitionBattleEnter;
            if (catalog.getItemFromGuid(guid) == null)
                fadeScreenColorTweener.Update();

            if (!fadeScreenColorTweener.IsPlayTween && !owner.mapScene.IsWiping())
            {
                owner.mapScene.SetWipe(Guid.Empty);
                fadeScreenColorTweener.Begin(new Color(Color.Black, 255), new Color(Color.Black, 0), 30);

                openingBackgroundImageScaleTweener.Clear();
                openingBackgroundImageScaleTweener.Add(1.2f, 1.0f, 30);
                openingBackgroundImageScaleTweener.Begin();

                battleViewer.openingMonsterScaleTweener.Begin(1.2f, 1.0f, 30);
                battleViewer.openingColorTweener.Begin(new Color(Color.White, 0), new Color(Color.White, 255), 30);

                LoadBattleSceneImpl();
                StartBattle();

                battleEvents.start(Rom.Script.Trigger.BATTLE_START);
                battleEvents.start(Rom.Script.Trigger.BATTLE_PARALLEL);

                IsDrawingBattleScene = true;
                ((BattleViewer3D)battleViewer).Show();

                ChangeBattleState(BattleState.StartFadeIn);

                // #13413 暗転をカットできる可能性があるので、バトルイベントを先行で1フレーム回してみる
                // #13413 There is a possibility of cutting the blackout, so try playing the battle event one frame in advance.
                battleEvents.update();

                // #14971 トランジションで画面を止めるよう修正
                // #14971 Fixed to stop screen at transition
                TransitionUtil.instance.reserveClearFB();
            }
        }

        private void StartBattle()
        {
            battleViewer.ClearDisplayMessage();
            if (!string.IsNullOrEmpty(battleStartWord) && enemyData.Count > 0)
            {
                battleViewer.SetDisplayMessage(string.Format(battleStartWord, enemyData[0].Name));
                battleViewer.OpenWindow(WindowType.MessageWindow);
                elapsedTimeForBattleStart = 0f;
            }
        }

        private void UpdateBattleState_StartFadeIn()
        {
            fadeScreenColorTweener.Update();

            openingBackgroundImageScaleTweener.Update();

            if (!fadeScreenColorTweener.IsPlayTween)
            {
                ChangeBattleState(BattleState.BattleSetting);
            }
        }

        private void UpdateBattleState_BattleSetting()
        {
            if (battleViewer.IsVisibleWindow(WindowType.MessageWindow))
            {
                elapsedTimeForBattleStart += GameMain.getRelativeParam60FPS();
                if (elapsedTimeForBattleStart > TIME_FOR_BATTLE_START_MESSEGE)
                {
                    battleViewer.OpenWindow(WindowType.None);
                }
                return;
            }



            ChangeBattleState(BattleState.BattleStart);
        }

        private void UpdateBattleState_BattleStart()
        {
            if (owner.mapScene.isToastVisible() || !isReady3DCamera())
            {
                return;
            }

            (battleViewer as BattleViewer3D)?.restoreCamera();
            battleEvents.start(Rom.Script.Trigger.BATTLE_TURN);
            totalTurn = 1;
            
            // Pokemon-style: Start pre-battle showcase showing both full teams
            if (!isPreBattleShowcase)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", "Initializing pre-battle showcase...");
                StartPreBattleShowcase();
                isPreBattleShowcase = true;
                preBattleShowcaseTimer = 0f;
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", "Started pre-battle showcase - showing both full teams!");
                return; // Stay in BattleStart state during showcase
            }
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", 
                string.Format("Showcase timer: {0:F1} / {1:F1} seconds", 
                preBattleShowcaseTimer / 60f, PRE_BATTLE_SHOWCASE_DURATION));
            
            // Update showcase timer
            preBattleShowcaseTimer += GameMain.getRelativeParam60FPS();
            
            // Update display message based on timer
            if (preBattleShowcaseTimer < 60f)
            {
                battleViewer.SetDisplayMessage("Team Lineups", WindowType.None);
            }
            else
            {
                battleViewer.SetDisplayMessage("Team Lineups - Press any key to continue", WindowType.None);
            }
            
            // Check for input to skip showcase early (but only after 1 second to prevent accidental skips)
            bool canSkip = preBattleShowcaseTimer >= 60f; // Allow skip after 1 second
            bool skipShowcase = canSkip && (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU) ||
                              Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.CANCEL, Input.GameState.MENU));
            
            // After showcase duration OR user input, transition to Pokemon-style 1v1 battle
            if (preBattleShowcaseTimer >= PRE_BATTLE_SHOWCASE_DURATION * 60f || skipShowcase) // Convert to frames
            {
                EndPreBattleShowcaseAndStartPokemonBattle();
                isPreBattleShowcase = false;
                ChangeBattleState(BattleState.Wait);
                
                if (skipShowcase)
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", "Showcase skipped by user input - transitioning to Pokemon-style 1v1 battle!");
                }
                else
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PreBattle", "Showcase finished - transitioning to Pokemon-style 1v1 battle!");
                }
            }

            var firstAttackRate = party.CalcConditionFirstAttackRate();

            if ((0 < firstAttackRate) && (battleRandom.Next(100) < firstAttackRate))
            {
                firstAttackType = FirstAttackType.Player;

                if (!string.IsNullOrEmpty(gameSettings.glossary.battle_player_first_attack))
                {
                    owner.mapScene.ShowToast(gameSettings.glossary.battle_player_first_attack);
                }
            }
            else if ((firstAttackRate < 0) && (battleRandom.Next(100) < -firstAttackRate))
            {
                firstAttackType = FirstAttackType.Monster;

                if (!string.IsNullOrEmpty(gameSettings.glossary.battle_monster_first_attack))
                {
                    owner.mapScene.ShowToast(gameSettings.glossary.battle_monster_first_attack);
                }
            }
            else
            {
                firstAttackType = FirstAttackType.None;
            }
        }

        private void UpdateBattleState_Wait()
        {
            if (owner.mapScene.isToastVisible())
            {
                return;
            }

            if (isReady3DCamera() && !battleEvents.isBusy())
            {
                var battleResult = CheckBattleFinish();

                if (battleResult == BattleResultState.NonFinish)
                {
                    // すでに何らかのバトル行動が入っている場合、バトル開始イベントで「即時行動」指定を仕込んだと思われる
                    // If some kind of battle action is already included, it seems that the \
                    // CTBバトルでは不要
                    // Not necessary for CTB battles
                    //if (battleEntryCharacters.Count > 0)
                    //{
                    //    ChangeBattleState(BattleState.ReadyExecuteCommand);
                    //}
                    //else
                    {
                        // 先制攻撃？
                        // Preemptive attack?
                        switch (firstAttackType)
                        {
                            case FirstAttackType.Player:// 先制した / took the lead
                                firstAttackType = FirstAttackType.None;

                                foreach (var player in playerData)
                                {
                                    ((ExBattlePlayerData)player).turnGauge = 1;
                                }

                                //ChangeBattleState(BattleState.PlayerTurnStart);
                                break;
                            case FirstAttackType.Monster:// 先制された / preempted
                                firstAttackType = FirstAttackType.None;

                                foreach (var monsterData in enemyData)
                                {
                                    ((ExBattleEnemyData)monsterData).turnGauge = 1;
                                }

                                //SkipPlayerBattleCommand();
                                break;
                            case FirstAttackType.None:
                            default:
                                ChangeBattleState(BattleState.LockInEquipment);
                                break;
                        }
                    }
                }
                else
                {
                    battleEvents.setBattleResult(battleResult);
                    ChangeBattleState(BattleState.StartBattleFinishEvent);
                }
            }
        }

        private void UpdateBattleState_LockInEquipment()
        {
            // 外された装備を袋に仕舞う
            // Put away the removed equipment in the bag
            if (!owner.mapScene.IsTrashVisible())
            {
                var isShowTrashWindow = false;

                foreach (var player in playerData)
                {
                    var hero = player.player;

                    while (hero.releaseEquipments.Count > 0)
                    {
                        var equipment = hero.releaseEquipments[0];

                        hero.releaseEquipments.RemoveAt(0);

                        if ((party.GetItemNum(equipment.guId) == 0) && (party.checkInventoryEmptyNum() <= 0))
                        {
                            owner.mapScene.ShowTrashWindow(equipment.guId, 1);

                            isShowTrashWindow = true;

                            break;
                        }
                        else
                        {
                            party.AddItem(equipment.guId, 1);
                        }
                    }

                    if (isShowTrashWindow)
                    {
                        break;
                    }
                }

                if (!isShowTrashWindow)
                {
                    activeCharacter = null;
                    ChangeBattleState(BattleState.PlayerTurnStart);
                }
            }
        }



        // 📉 - START - Status Ailment - Change Stat Change Decay
        private void UpdateBattleState_PlayerTurnStart()
        {
            if (battleEvents.isBusy())
                return;

            recoveryStatusInfo.Clear();

            // 1回目のコマンド選択時のみ 状態異常回復判定 & 強化用ステータス減衰
            // Only when the command is selected for the first time Status abnormality recovery judgment & strengthening status attenuation
                foreach (var character in createBattleCharacterList())
            {
                // 状態異常 回復判定
                // Status Abnormal Recovery Judgment
                var recoveryList = new List<Hero.ConditionInfo>(character.conditionInfoDic.Count);

                foreach (var e in character.conditionInfoDic)
                {
                    var info = e.Value;

                    if ((info.recovery & Hero.ConditionInfo.RecoveryType.Probability) != 0)
                    {
                        if (battleRandom.Next(100) < info.probabilityRate)
                        {
                            recoveryList.Add(info);

                            continue;
                        }
                    }

                    if ((info.recovery & Hero.ConditionInfo.RecoveryType.Turn) != 0)
                    {
                        info.turnCount--;

                        if (info.turnCount <= 0)
                        {
                            recoveryList.Add(info);

                            continue;
                        }
                    }
                }

                foreach (var info in recoveryList)
                {
                    character.RecoveryCondition(info.condition, battleEvents, Rom.Condition.RecoveryType.Terms);

                    if (info.rom != null)
                    {
                        recoveryStatusInfo.Add(new RecoveryStatusInfo(character, info.rom));

						SetNextBattleStatus(character);
					}
                }
            }

            foreach (var player in playerData)
            {
                UpdateEnhanceEffect(player.attackEnhanceEffects);
                UpdateEnhanceEffect(player.guardEnhanceEffects);

                // 強化用のステータスを減衰させる
                // Attenuates stats for enhancement
                const float DampingRate = 1f;
                player.MaxHitPointEnhance = (int)(player.MaxHitPointEnhance * DampingRate);
                player.MaxMagicPointEnhance = (int)(player.MaxMagicPointEnhance * DampingRate);
                player.PowerEnhancement = (int)(player.PowerEnhancement * DampingRate);
                player.VitalityEnhancement = (int)(player.VitalityEnhancement * DampingRate);
                player.MagicEnhancement = (int)(player.MagicEnhancement * DampingRate);
                player.SpeedEnhancement = (int)(player.SpeedEnhancement * DampingRate);
                player.EvasionEnhancement = (int)(player.EvasionEnhancement * DampingRate);
                player.DexterityEnhancement = (int)(player.DexterityEnhancement * DampingRate);

                player.enhanceStatusValue.MulStatusAll(DampingRate);

                foreach (var element in player.ResistanceAttackAttributeEnhance.Keys.ToArray())
                {
                    player.ResistanceAttackAttributeEnhance[element] = (int)(player.ResistanceAttackAttributeEnhance[element] * DampingRate);
                }

                foreach (var element in player.ResistanceAilmentEnhance.Keys.ToArray())
                {
                    player.ResistanceAilmentEnhance[element] = (int)(player.ResistanceAilmentEnhance[element] * DampingRate);
                }

                SetBattleStatusData(player, true);

                battleViewer.SetPlayerStatusEffect(player);
            }

            foreach (var monster in enemyData)
            {
                // 強化用のステータスを減衰させる
                // Attenuates stats for enhancement
                const float DampingRate = 1f;
                monster.MaxHitPointEnhance = (int)(monster.MaxHitPointEnhance * DampingRate);
                monster.MaxMagicPointEnhance = (int)(monster.MaxMagicPointEnhance * DampingRate);
                monster.PowerEnhancement = (int)(monster.PowerEnhancement * DampingRate);
                monster.VitalityEnhancement = (int)(monster.VitalityEnhancement * DampingRate);
                monster.MagicEnhancement = (int)(monster.MagicEnhancement * DampingRate);
                monster.SpeedEnhancement = (int)(monster.SpeedEnhancement * DampingRate);
                monster.EvasionEnhancement = (int)(monster.EvasionEnhancement * DampingRate);
                monster.DexterityEnhancement = (int)(monster.DexterityEnhancement * DampingRate);

                monster.enhanceStatusValue.MulStatusAll(DampingRate);

                foreach (var element in monster.ResistanceAttackAttributeEnhance.Keys.ToArray())
                {
                    monster.ResistanceAttackAttributeEnhance[element] = (int)(monster.ResistanceAttackAttributeEnhance[element] * DampingRate);
                }

                foreach (var element in monster.ResistanceAilmentEnhance.Keys.ToArray())
                {
                    monster.ResistanceAilmentEnhance[element] = (int)(monster.ResistanceAilmentEnhance[element] * DampingRate);
                }
            }

            UpdatePosition();
            ChangeBattleState(BattleState.CheckTurnRecoveryStatus);
        }
        // 📉 - END - Status Ailment - Change Stat Change Decay

        // 🗑️ REMOVED: StatusUpdateImpl function was orphaned and contained duplicate/outdated logic
        // The functionality has been properly integrated into UpdateBattleState_PlayerTurnStart() above

        private void UpdateBattleState_CheckTurnRecoveryStatus()
        {
            if (IsUpdateBattleStatusDataAll())
            {
                return;
            }

            if (recoveryStatusInfo.Count == 0)
            {
                if (activeCharacter != null)
                {
                ChangeBattleState(BattleState.CheckBattleCharacterDown3);
            }
            else
            {
                    ChangeBattleState(WAIT_CTB_GAUGE);
                }
                //ChangeBattleState(BattleState.SetEnemyBattleCommand);
            }
            else
            {
                ((BattleViewer3D)battleViewer).RecoveryConditionMotion(recoveryStatusInfo[0]);
                battleViewer.SetDisplayMessage(recoveryStatusInfo[0].GetMessage(gameSettings));

                ChangeBattleState(BattleState.DisplayTurnRecoveryStatus);
            }
        }

        private void UpdateBattleState_DisplayTurnRecoveryStatus()
        {
            if (string.IsNullOrEmpty(battleViewer.displayMessageText) ||
                battleStateFrameCount >= 30 || Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU))
            {
                recoveryStatusInfo.RemoveAt(0);

                ChangeBattleState(BattleState.CheckTurnRecoveryStatus);
            }
        }

        private void UpdateBattleState_SelectActivePlayer()
        {
            // Count alive players that haven't selected commands yet
            var alivePlayersNeedingCommands = playerData.Where(p => !p.IsDeadCondition() && p.selectedBattleCommandType == BattleCommandType.Undecided).ToList();
            
            if (alivePlayersNeedingCommands.Count == 0)
            {
                // All players have selected their actions - transition to enemy phase
                commandSelectPlayer = null;
                commandSelectedMemberCount = 0;

                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Turn Planning", 
                    "All players have selected actions - starting enemy command selection");
                
                StartEnemyCommandSelectionPhase();
            }
            else
            {
                // Select next player that needs to choose an action
                commandSelectPlayer = alivePlayersNeedingCommands[0];
                commandSelectPlayer.commandSelectedCount++;
                
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Turn Planning", 
                    string.Format("Player {0} selecting action ({1} players remaining)", 
                    commandSelectPlayer.Name, alivePlayersNeedingCommands.Count));

                ChangeBattleState(BattleState.CheckCommandSelect);
            }
        }

        private void UpdateBattleState_CheckCommandSelect()
        {
            // 現在の状態異常に合わせて行動させる
            // Act according to the current abnormal state
            if (commandSelectPlayer.IsDeadCondition())
            {
                commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Nothing_Down;
                ChangeBattleState(BattleState.SetPlayerBattleCommandTarget);
            }
            else
            {
                var setConditionBattleCommandType = false;

                foreach (var e in commandSelectPlayer.conditionInfoDic)
                {
                    var condition = e.Value.rom;

                    if (condition != null)
                    {
                        if (condition.IsActionDisabled)
                        {
                            commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Nothing;

                            setConditionBattleCommandType = true;
                            break;
                        }
                        else if (condition.IsAutoAttack)
                        {
                            commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Attack;

                            setConditionBattleCommandType = true;

                            // 行動不能が優先なので、ここでは終わらない
                            // Incapacity is the priority, so it doesn't end here
                        }
                    }
                }

                if (setConditionBattleCommandType)
                {
                    ChangeBattleState(BattleState.SetPlayerBattleCommandTarget);
                }
                else
                {
                    var enable = commandSelectPlayer.IsCommandSelectable;

					if (enable && commandSelectPlayer.IsDisabledAllCommand)
					{
                        enable = IsFirstCommandSelectPlayer && !IsDisabledEscape;
					}

                    if (!enable)
                    {
                        // #13124 で修正 バトルイベントでコマンドをセットした場合に対応
                        // Fixed in #13124 Compatible with setting commands in battle events.
                        ChangeBattleState(BattleState.SetPlayerBattleCommandTarget);

                        // #13124 でコメントアウト
                        // Comment out with #13124
                        //ResetCtbGauge(activeCharacter);
                        //ChangeBattleState(WAIT_CTB_GAUGE);

                        // 元の処理
                        // original processing
                        //commandSelectedMemberCount++;
                        //ChangeBattleState(BattleState.SelectActivePlayer);
                    }
                    else
                    {
                        Audio.PlaySound(owner.se.menuOpen);
                        ChangeBattleState(BattleState.SetPlayerBattleCommand);
                    }
                }
            }
        }

        private void UpdateBattleState_SetPlayerBattleCommand()
        {
            if (battleEvents.isBusy())
                return;
            battleEvents.clearCurrentProcessingTrigger();

            playerBattleCommand.Clear();
            battleViewer.battleCommandChoiceWindowDrawer.ClearChoiceListData();



            // 戦闘用コマンドの登録
            // Registering Combat Commands

            {
                var enableAttack = false;

                foreach (var enemy in enemyData)
                {
                    if ((enemy.HitPoint > 0) && IsHitRange(commandSelectPlayer, enemy))
                    {
                        enableAttack = true;

                        break;
                    }
                }

                foreach (var guid in commandSelectPlayer.player.rom.battleCommandList)
                {
                    var command = (Rom.BattleCommand)catalog.getItemFromGuid(guid);

                    if (command.type == BattleCommand.CommandType.NONE)
                    {
                        continue;
                    }

                    playerBattleCommand.Add(command);

                    var enable = true;

                    switch (command.type)
                    {
                        case BattleCommand.CommandType.ATTACK:
                            enable = enableAttack;
                            break;
                        case BattleCommand.CommandType.POSITION:
                            // Pokemon-style: Enable if there are reserve party members to switch with
                            enable = stockPlayerData.Count > 0;
                            break;
                        default:
                            break;
                    }

					if (enable)
					{
                        enable = !commandSelectPlayer.IsBattleCommandDisabled(command);
                    }


                    if (iconTable.ContainsKey(command.icon.guId))
                    {
                        battleViewer.battleCommandChoiceWindowDrawer.AddChoiceData(iconTable[command.icon.guId], command.icon, command.name, enable);
                    }
                    else
                    {
                        battleViewer.battleCommandChoiceWindowDrawer.AddChoiceData(command.name, enable);
                    }
                }
            }

            // Pokemon-style: Add Pokemon command if there are reserve party members
            if (stockPlayerData.Count > 0)
            {
                playerBattleCommand.Add(new BattleCommand() { type = BattleCommand.CommandType.POSITION, name = "Pokemon" });
                
                // Try to use return icon for pokemon switch, or just use text
                if (iconTable.ContainsKey(gameSettings.returnIcon.guId))
                {
                    battleViewer.battleCommandChoiceWindowDrawer.AddChoiceData(iconTable[gameSettings.returnIcon.guId], gameSettings.returnIcon, "Pokemon", true);
                }
                else
                {
                    battleViewer.battleCommandChoiceWindowDrawer.AddChoiceData("Pokemon", true);
                }
            }

            // 1番目にコマンドを選択できるメンバーだけ「逃げる」コマンドを追加する
            // Add the \
            // それ以外のメンバーには「戻る」コマンドを追加する
            // Add \
            //if (IsFirstCommandSelectPlayer)
            {
                // 「逃げる」コマンドの登録
                // Registering the \
                playerBattleCommand.Add(new BattleCommand() { type = BattleCommand.CommandType.ESCAPE });

                battleViewer.battleCommandChoiceWindowDrawer.AddChoiceData(battleViewer.battleCommandChoiceWindowDrawer.escapeImageId, battleViewer.battleCommandChoiceWindowDrawer.escapeIcon, "Run", (escapeAvailable && !IsDisabledEscape));
                //battleViewer.battleCommandChoiceWindowDrawer.AddChoiceData(battleViewer.battleCommandChoiceWindowDrawer.escapeImageId, gameSettings.glossary.battle_escape_command, escapeAvailable);
            }
            //else
            //{
            //    // 「戻る」コマンドの登録
            // // register \
            //    playerBattleCommand.Add(new BattleCommand() { type = BattleCommand.CommandType.BACK });

            //    //battleViewer.battleCommandChoiceWindowDrawer.AddChoiceData(iconTable[gameSettings.returnIcon.guId], gameSettings.returnIcon, gameSettings.glossary.battle_back);
            //    battleViewer.battleCommandChoiceWindowDrawer.AddChoiceData(battleViewer.battleCommandChoiceWindowDrawer.returnImageId, gameSettings.glossary.battle_back);
            //}

            if (battleViewer.battleCommandChoiceWindowDrawer.ChoiceItemCount < BattlePlayerData.RegisterBattleCommandCountMax)
            {
                battleViewer.battleCommandChoiceWindowDrawer.RowCount = BattlePlayerData.RegisterBattleCommandCountMax;
            }
            else
            {
                battleViewer.battleCommandChoiceWindowDrawer.RowCount = battleViewer.battleCommandChoiceWindowDrawer.ChoiceItemCount;
            }

            if (battleViewer.battleCommandChoiceWindowDrawer.ChoiceItemCount > 1)
            {
                battleEvents.start(Rom.Script.Trigger.BATTLE_BEFORE_COMMAND_SELECT);
                ChangeBattleState(BattleState.WaitEventsBeforeCommandSelect);
            }
            else
            {
                commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Nothing;
                ChangeBattleState(BattleState.SetPlayerBattleCommandTarget);
            }
        }

        private void UpdateBattleState_WaitEventsBeforeCommandSelect()
        {
            if (!battleEvents.isBusy())
            {
                battleEvents.clearCurrentProcessingTrigger();

                commandSelectPlayer.characterImageTween.Begin(Vector2.Zero, new Vector2(30, 0), 5);
                commandSelectPlayer.ChangeEmotion(Resource.Face.FaceType.FACE_ANGER);

                battleViewer.battleCommandChoiceWindowDrawer.SelectDefaultItem(commandSelectPlayer, battleState);

                commandSelectPlayer.statusWindowState = StatusWindowState.Active;

                Vector2 commandWindowPosition = commandSelectPlayer.commandSelectWindowBasePosition;

                if (battleViewer.battleCommandChoiceWindowDrawer.ChoiceItemCount > BattlePlayerData.RegisterBattleCommandCountMax && commandSelectPlayer.viewIndex >= 2)
                {
                    commandWindowPosition.Y -= 50;
                }

                battleViewer.CommandWindowBasePosition = commandWindowPosition;
                battleViewer.BallonImageReverse = commandSelectPlayer.isCharacterImageReverse;

                battleViewer.OpenWindow(WindowType.PlayerCommandWindow);

                ChangeBattleState(BattleState.SelectPlayerBattleCommand);
                battleCommandState = SelectBattleCommandState.CommandSelect;
            }
        }

        private void UpdateBattleState_SelectPlayerBattleCommand()
        {
            bool isChange = false;

            if (battleCommandState == SelectBattleCommandState.CommandEnd && commandSelectPlayer.selectedBattleCommandType != BattleCommandType.Back)
            {
                isChange = true;

                battleCommandState = SelectBattleCommandState.None;

                if (commandSelectPlayer.selectedBattleCommandType == BattleCommandType.PlayerEscape)
                {
                    battleEvents.start(Rom.Script.Trigger.BATTLE_AFTER_COMMAND_SELECT);
                    SkipPlayerBattleCommand(true);
                }
                else
                {
                    ChangeBattleState(BattleState.SetPlayerBattleCommandTarget);
                }
            }

            // 自分のコマンド選択をキャンセルしてひとつ前の人のコマンド選択に戻る
            // Cancel your own command selection and return to the previous person's command selection
            if (battleCommandState == SelectBattleCommandState.CommandCancel || (battleCommandState == SelectBattleCommandState.CommandEnd && commandSelectPlayer.selectedBattleCommandType == BattleCommandType.Back))
            {
                bool isBackCommandSelect = true;
                int prevPlayerIndex = 0;

                // 戻れる？
                // Can you go back?
                //for (int index = commandSelectedMemberCount - 1; index >= 0; index--)
                //{
                //    if (index < playerData.Count && playerData[index].IsAnyCommandSelectable)
                //    {
                //        isBackCommandSelect = true;
                //        prevPlayerIndex = index;
                //        break;
                //    }
                //}

                // 戻る
                // return
                if (isBackCommandSelect)
                {
                    isChange = true;
                    battleEvents.start(Rom.Script.Trigger.BATTLE_CANCEL_COMMAND_SELECT);
                    //commandSelectedMemberCount = prevPlayerIndex;
                    ChangeBattleState(BattleState.SetPlayerBattleCommand);
                }
                else
                {
                    battleCommandState = SelectBattleCommandState.CommandSelect;
                }
            }

            if (isChange)
            {
                commandSelectPlayer.statusWindowState = StatusWindowState.Wait;
                commandSelectPlayer.characterImageTween.Begin(Vector2.Zero, 5);
                commandSelectPlayer.ChangeEmotion(Resource.Face.FaceType.FACE_NORMAL);

                //commandSelectPlayer = playerData[commandSelectedMemberCount];

                battleViewer.CloseWindow();
            }
        }

        private void UpdateBattleState_SetPlayerBattleCommandTarget()
        {
            if (!commandSelectPlayer.forceSetCommand)// 行動の強制指定がある場合はターゲット更新不要 / If there is a forced action specification, there is no need to update the target.
                commandSelectPlayer.targetCharacter = GetTargetCharacters(commandSelectPlayer);

            commandSelectedMemberCount++;

            // エフェクト先読み
            // Effect lookahead
            GetCommandEffectImpl(commandSelectPlayer, catalog, out var efA, out var efB);
            preloadEffect(efA);
            preloadEffect(efB);

            battleEvents.start(Rom.Script.Trigger.BATTLE_AFTER_COMMAND_SELECT);
            commandSelectPlayer = null;
            
            // TURN-BASED PLANNING: Continue with player selection or move to enemy phase
            ChangeBattleState(BattleState.SelectActivePlayer);
        }

        private class EffectPreloadJob : SharpKmyBase.Job
        {
            private static Dictionary<Guid, SharpKmyGfx.ParticleInstance> preloadTargets = new Dictionary<Guid, SharpKmyGfx.ParticleInstance>();
            private Guid guid;
            private Catalog catalog;

            public EffectPreloadJob(Guid guid, Catalog catalog)
            {
                this.guid = guid;
                this.catalog = catalog;
            }

            public override void func()
            {
                var pt = catalog.getItemFromGuid(guid) as Resource.Particle;
                while (pt != null)
                {
                    if (preloadTargets.ContainsKey(guid))
                        break;

                    var inst = new SharpKmyGfx.ParticleInstance(pt.path, 0);
                    pt = catalog.getItemFromGuid(pt.BattleSettingChainTarget) as Resource.Particle;
                    preloadTargets.Add(guid, inst);
                }
            }

            public static void clearPreloads()
            {
                foreach(var inst in preloadTargets.Values)
                {
                    inst.Release();
                }
                preloadTargets.Clear();
            }
        }

        private void preloadEffect(Guid guid)
        {
            var job = new EffectPreloadJob(guid, catalog);
            SharpKmyBase.Job.addJob(job);
        }

        /// <summary>
        /// CTB バトルでは通りません
        /// Not passing in CTB battles
        /// </summary>
        // ⚡ - START - Priority Attacks
        private void UpdateBattleState_SortBattleActions()
        {
            if (battleEvents.isBusy())
                return;
            battleEvents.clearCurrentProcessingTrigger();

            commandExecuteMemberCount = 0;

            // Create the initial character list, excluding those with Nothing_Down command
            // Nothing_Downコマンドを持つ文字を除いた初期文字リストを作成する。
            var characters = createBattleCharacterList()
                .Where(character => character.selectedBattleCommandType != BattleCommandType.Nothing_Down)
                .ToList();

            // HIGHEST PRIORITY: Escape and Switching (Position) commands - these should ALWAYS go first
            // 最優先：エスケープとスイッチング（ポジション）コマンド - これらは常に最初に実行される
            AddBattleActionEntry(characters.Where(character => character.selectedBattleCommandType == BattleCommandType.PlayerEscape));
            AddBattleActionEntry(characters.Where(character => character.selectedBattleCommandType == BattleCommandType.MonsterEscape));
            AddBattleActionEntry(characters.Where(character => character.selectedBattleCommandType == BattleCommandType.Position)
                .OrderByDescending(character => character.Speed)); // Sort by speed for switching priority
            
            // SECOND PRIORITY: Guard commands
            // 第二優先：ガードコマンド
            AddBattleActionEntry(characters.Where(character => character.selectedBattleCommandType == BattleCommandType.Guard)
                .OrderBy(character => character.UniqueID));

            // Filter out characters already added (those with escape or guard commands)
            // すでに追加された文字（エスケープやガードコマンドを含む文字）を除外する。
            var remainingCharacters = characters
                .Where(character => !battleEntryCharacters.Exists(x => x.character == character))
                .ToList();

            // Tier 1: #highpriority (highest priority, sorted by Speed)
            // ティア1：#highpriority（最優先、スピード順）
            var highPriorityCharacters = remainingCharacters
                .Where(character => character.selectedSkill?.tags?.Contains("#highpriority") ?? false)
                .OrderByDescending(character => character.Speed);
            AddBattleActionEntry(highPriorityCharacters);

            // Tier 2: Normal skills (no #highpriority or #lowpriority, sorted by Speed)
            // ティア2：ノーマルスキル（#highpriority、#lowpriorityなし、スピード順）
            var normalCharacters = remainingCharacters
                .Where(character => 
                    !(character.selectedSkill?.tags?.Contains("#highpriority") ?? false) &&
                    !(character.selectedSkill?.tags?.Contains("#lowpriority") ?? false))
                .OrderByDescending(character => character.Speed);
            AddBattleActionEntry(normalCharacters);

            // Tier 3: #lowpriority (lowest priority, sorted by Speed)
            // ティア3：#lowpriority（優先順位が最も低い、スピード順）
            var lowPriorityCharacters = remainingCharacters
                .Where(character => character.selectedSkill?.tags?.Contains("#lowpriority") ?? false)
                .OrderByDescending(character => character.Speed);
            AddBattleActionEntry(lowPriorityCharacters);

            // Log the final action order for debugging
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Priority System", 
                string.Format("Action order determined: {0} total characters", battleEntryCharacters.Count));
            
            for (int i = 0; i < battleEntryCharacters.Count; i++)
            {
                var character = battleEntryCharacters[i].character;
                var priorityText = GetPriorityText(character);
                var actionName = GetActionName(character);
                
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Priority System", 
                    string.Format("#{0}: {1} (Speed: {2}, Priority: {3}) - {4}", 
                    i + 1, character.Name, character.Speed, priorityText, actionName));
            }

            // CRITICAL FIX: Set activeCharacter to first character in action queue before execution
            if (battleEntryCharacters.Count > 0)
            {
                activeCharacter = battleEntryCharacters[0].character;
                commandSelectPlayer = activeCharacter as ExBattlePlayerData;
                activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.NONE;
                battleEvents.start(Rom.Script.Trigger.BATTLE_TURN);
                activeCharacter.InitializeBattleCommandDisabled(catalog);
                
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Priority System", 
                    string.Format("Set activeCharacter to: {0} (first in execution queue)", activeCharacter.Name));
            }
            else
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Priority System", 
                    "Warning: No characters in execution queue!");
            }

            ChangeBattleState(BattleState.ReadyExecuteCommand);
        }
        // ⚡ - END - Priority Attacks

        private void AddBattleActionEntry(IEnumerable<BattleCharacterBase> enumerable)
        {
            battleEntryCharacters.AddRange(enumerable.Select(x => new BattleActionEntry(x)));
        }

        /// <summary>
        /// Get priority text for logging purposes
        /// </summary>
        /// <param name="character">Character to check priority for</param>
        /// <returns>Priority description string</returns>
        private string GetPriorityText(BattleCharacterBase character)
        {
            switch (character.selectedBattleCommandType)
            {
                case BattleCommandType.PlayerEscape:
                case BattleCommandType.MonsterEscape:
                    return "ESCAPE (HIGHEST)";
                case BattleCommandType.Position:
                    return "SWITCH (HIGHEST)";
                case BattleCommandType.Guard:
                    return "GUARD";
                default:
                    if (character.selectedSkill?.tags?.Contains("#highpriority") ?? false)
                        return "HIGH";
                    else if (character.selectedSkill?.tags?.Contains("#lowpriority") ?? false)
                        return "LOW";
                    else
                        return "NORMAL";
            }
        }

        /// <summary>
        /// Get action name for logging purposes
        /// </summary>
        /// <param name="character">Character to get action name for</param>
        /// <returns>Action name string</returns>
        private string GetActionName(BattleCharacterBase character)
        {
            if (character.selectedBattleCommandType == BattleCommandType.Skill && character.selectedSkill != null)
                return character.selectedSkill.name;
            else
                return character.selectedBattleCommandType.ToString();
        }

        /// <summary>
        /// Check if a condition/status ailment has the #switchclear tag
        /// </summary>
        /// <param name="condition">The condition ROM data to check</param>
        /// <returns>True if the condition should be cleared on switch</returns>
        /// <example>
        /// Usage in the editor:
        /// - Status name: "Attack Up #switchclear" → Clears on switch
        /// - Status name: "Poison" with no tags → Persists on switch
        /// - Status description: "Temporary boost that ends when switching out #switchclear" → Clears on switch
        /// </example>
        private bool HasSwitchClearTag(Rom.Condition condition)
        {
            if (condition == null) return false;
            
            // SIMPLE SOLUTION: Check condition name for #switchclear tag
            // Since management tags from editor are not accessible at runtime,
            // we need to put the tag directly in the condition name or use an alternative approach
            
            bool hasTagInName = !string.IsNullOrEmpty(condition.name) && 
                               condition.name.ToLowerInvariant().Contains("#switchclear");
            
            if (hasTagInName)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                    string.Format("FOUND #switchclear tag in condition name: '{0}'", condition.name));
                return true;
            }
            
            // Alternative: Check if there are any message fields that might contain the tag
            var messageFields = new[] { "MessageForAlly", "MessageForEnemy", "MessageForContinue", "MessageForFinished" };
            
            foreach (var fieldName in messageFields)
            {
                try
                {
                    var prop = condition.GetType().GetProperty(fieldName);
                    if (prop != null)
                    {
                        var value = prop.GetValue(condition)?.ToString();
                        if (!string.IsNullOrEmpty(value) && value.ToLowerInvariant().Contains("#switchclear"))
                        {
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                                string.Format("FOUND #switchclear tag in {0}: '{1}'", fieldName, value));
                            return true;
                        }
                    }
                }
                catch { }
            }
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                string.Format("No #switchclear tag found in condition '{0}'. " + 
                "SOLUTION: Add #switchclear to the condition NAME in the editor (e.g., 'Agility Down #switchclear')", 
                condition.name));
            
            return false;
        }
        
        /// <summary>
        /// Dump the complete structure of an object for debugging
        /// </summary>
        private void DumpObjectStructure(object obj, string name, int depth, int maxDepth)
        {
            if (obj == null || depth > maxDepth) return;
            
            try
            {
                string indent = new string(' ', depth * 2);
                var objType = obj.GetType();
                
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                    string.Format("{0}{1} ({2})", indent, name, objType.Name));
                
                var properties = objType.GetProperties();
                foreach (var prop in properties)
                {
                    try
                    {
                        // Skip problematic properties
                        if (prop.GetIndexParameters().Length > 0) continue;
                        if (prop.Name == "SyncRoot" || prop.Name == "Item") continue;
                        
                        var value = prop.GetValue(obj);
                        string valueStr = value?.ToString() ?? "null";
                        
                        // Truncate very long values
                        if (valueStr.Length > 100)
                            valueStr = valueStr.Substring(0, 100) + "...";
                            
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                            string.Format("{0}  {1}: {2}", indent, prop.Name, valueStr));
                        
                        // Check if this property contains the tag
                        if (value is string strVal && !string.IsNullOrEmpty(strVal) && 
                            strVal.ToLowerInvariant().Contains("#switchclear"))
                        {
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                                string.Format("{0}  *** TAG FOUND IN PROPERTY: {1} ***", indent, prop.Name));
                        }
                        
                        // Recursively dump complex objects
                        if (depth < maxDepth && value != null && !prop.PropertyType.IsPrimitive && 
                            prop.PropertyType != typeof(string) && prop.PropertyType != typeof(decimal) && 
                            prop.PropertyType != typeof(DateTime) && prop.PropertyType != typeof(Guid) &&
                            !prop.PropertyType.IsEnum && prop.PropertyType.Namespace?.StartsWith("Yukar") == true)
                        {
                            DumpObjectStructure(value, prop.Name, depth + 1, maxDepth);
                        }
                    }
                    catch (Exception ex)
                    {
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                            string.Format("{0}  {1}: ERROR - {2}", indent, prop.Name, ex.Message));
                    }
                }
            }
            catch (Exception ex)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                    string.Format("Error dumping {0}: {1}", name, ex.Message));
            }
        }
        
        /// <summary>
        /// Recursively search an object and all its properties for a specific tag
        /// </summary>
        /// <param name="obj">Object to search</param>
        /// <param name="tag">Tag to search for</param>
        /// <param name="path">Current property path (for debugging)</param>
        /// <param name="depth">Current recursion depth</param>
        /// <param name="maxDepth">Maximum recursion depth</param>
        /// <returns>True if tag is found</returns>
        private bool SearchObjectForTag(object obj, string tag, string path, int depth, int maxDepth)
        {
            if (obj == null || depth > maxDepth) return false;
            
            try
            {
                var objType = obj.GetType();
                
                // Check if object itself is a string containing the tag
                if (objType == typeof(string))
                {
                    string strValue = obj.ToString();
                    if (!string.IsNullOrEmpty(strValue) && strValue.ToLowerInvariant().Contains(tag.ToLowerInvariant()))
                    {
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                            string.Format("FOUND tag '{0}' in string at path '{1}': '{2}'", tag, path, strValue));
                        return true;
                    }
                }
                
                // Search all properties
                var properties = objType.GetProperties();
                foreach (var prop in properties)
                {
                    try
                    {
                        // Skip indexer properties and problematic properties
                        if (prop.GetIndexParameters().Length > 0) continue;
                        if (prop.Name == "SyncRoot" || prop.Name == "Item") continue;
                        
                        var value = prop.GetValue(obj);
                        if (value == null) continue;
                        
                        string currentPath = string.IsNullOrEmpty(path) ? prop.Name : path + "." + prop.Name;
                        
                        // Check string properties
                        if (value is string strVal)
                        {
                            if (!string.IsNullOrEmpty(strVal) && strVal.ToLowerInvariant().Contains(tag.ToLowerInvariant()))
                            {
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                                    string.Format("FOUND tag '{0}' in property '{1}': '{2}'", tag, currentPath, strVal));
                                return true;
                            }
                        }
                        // Check collections
                        else if (value is System.Collections.IEnumerable enumerable && !(value is string))
                        {
                            int index = 0;
                            foreach (var item in enumerable)
                            {
                                if (SearchObjectForTag(item, tag, currentPath + "[" + index + "]", depth + 1, maxDepth))
                                {
                                    return true;
                                }
                                index++;
                                if (index > 20) break; // Limit collection search to prevent infinite loops
                            }
                        }
                        // Recursively search complex objects (but avoid circular references)
                        else if (!prop.PropertyType.IsPrimitive && prop.PropertyType != typeof(decimal) && 
                                prop.PropertyType != typeof(DateTime) && prop.PropertyType != typeof(Guid) &&
                                !prop.PropertyType.IsEnum && prop.PropertyType.Namespace?.StartsWith("Yukar") == true)
                        {
                            if (SearchObjectForTag(value, tag, currentPath, depth + 1, maxDepth))
                            {
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log the error but continue searching
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                            string.Format("Error accessing property '{0}': {1}", prop.Name, ex.Message));
                    }
                }
            }
            catch (Exception ex)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                    string.Format("Error searching object at path '{0}': {1}", path, ex.Message));
            }
            
            return false;
        }
        
        /// <summary>
        /// Dynamic Dual-Type System: Calculate type effectiveness for dual-type attacks vs dual-type defenders
        /// Includes STAB (Same Type Attack Bonus) calculation
        /// </summary>
        /// <param name="attacker">The attacking character (to get skill type from tags)</param>
        /// <param name="target">The target monster/character</param>
        /// <returns>Damage multiplier based on dual-type calculation including STAB</returns>
        private static float CalculateDualTypeEffectiveness(BattleCharacterBase attacker, BattleCharacterBase target)
        {
            if (target == null || attacker == null) 
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Dual Type Debug", "Attacker or target is null");
                return 1.0f;
            }
            
            // DEBUG: Log what we're checking
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TYPE CALC", 
                string.Format("=== TYPE EFFECTIVENESS CHECK: {0} → {1} ===", attacker.Name, target.Name));
            
            // Extract type tags from attacker and target
            var attackerTypes = ExtractTypeTagsFromCharacter(attacker);
            var targetTypes = ExtractTypeTagsFromCharacter(target);
            var attackTypes = ExtractTypesFromSkill(attacker);
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TYPE CALC", 
                string.Format("Attacker {0} types: [{1}]", attacker.Name, string.Join(", ", attackerTypes)));
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TYPE CALC", 
                string.Format("Target {0} types: [{1}]", target.Name, string.Join(", ", targetTypes)));
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TYPE CALC", 
                string.Format("Attack types: [{0}]", string.Join(", ", attackTypes)));
            
            if (targetTypes.Count == 0) 
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TYPE CALC", "⚠️ NO TARGET TYPES FOUND - Add type tags to your Pokemon!");
                return 1.0f;
            }
            
            if (attackTypes.Count == 0) 
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TYPE CALC", "⚠️ NO ATTACK TYPES FOUND - Add type tags to your moves!");
                return 1.0f;
            }
            
            // STAB CALCULATION: Check if attacker's type matches any attack type
            float stabBonus = CalculateSTAB(attackerTypes, attackTypes);
            
            // For dual-type attacks, we calculate the AVERAGE effectiveness of both attack types
            // This prevents dual-type attacks from being overpowered
            float totalEffectiveness = 0.0f;
            var allCalculations = new List<string>();
            
            foreach (var attackType in attackTypes)
            {
                float attackTypeEffectiveness = 1.0f;
                var attackCalculations = new List<string>();
                
                // Calculate this attack type's effectiveness against all target types
                foreach (var defenseType in targetTypes)
                {
                    float typeMultiplier = GetTypeEffectiveness(attackType, defenseType);
                    attackTypeEffectiveness *= typeMultiplier;
                    
                    attackCalculations.Add(string.Format("{0} vs {1} = {2}x", attackType, defenseType, typeMultiplier));
                }
                
                totalEffectiveness += attackTypeEffectiveness;
                allCalculations.Add(string.Format("[{0}]: {1} = {2}x", 
                    attackType, string.Join(" × ", attackCalculations), attackTypeEffectiveness));
            }
            
            // Average the effectiveness of all attack types
            float typeEffectiveness = totalEffectiveness / attackTypes.Count;
            
            // Apply STAB bonus to final calculation
            float finalMultiplier = typeEffectiveness * stabBonus;
            
            // Log the comprehensive calculation including STAB
            if (stabBonus > 1.0f)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TYPE CALC", 
                    string.Format("✨ STAB BONUS: {0}x (Attacker types: [{1}] match attack types: [{2}])", 
                    stabBonus, string.Join(", ", attackerTypes), string.Join(", ", attackTypes)));
            }
            
            // ALWAYS log the final type effectiveness calculation for visibility
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TYPE CALC", 
                string.Format("📊 FINAL CALCULATION: {0} → STAB: {1}x → TOTAL: {2}x", 
                string.Join(" | ", allCalculations), stabBonus, finalMultiplier));
                
            // Add effectiveness message like Pokemon games
            if (finalMultiplier > 1.5f)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TYPE CALC", "🔥 It's super effective!");
            }
            else if (finalMultiplier < 0.75f && finalMultiplier > 0.0f)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TYPE CALC", "💧 It's not very effective...");
            }
            else if (finalMultiplier == 0.0f)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TYPE CALC", "❌ It has no effect!");
            }
            
            return finalMultiplier;
        }
        
        /// <summary>
        /// Calculate STAB (Same Type Attack Bonus) when attacker's type matches attack type
        /// </summary>
        /// <param name="attackerTypes">Attacker's types</param>
        /// <param name="attackTypes">Attack's types</param>
        /// <returns>STAB multiplier (1.0x for no STAB, 1.5x for STAB)</returns>
        private static float CalculateSTAB(List<string> attackerTypes, List<string> attackTypes)
        {
            const float STAB_MULTIPLIER = 1.5f;  // Standard Pokemon STAB bonus
            
            if (attackerTypes.Count == 0 || attackTypes.Count == 0) return 1.0f;
            
            // Check if any attacker type matches any attack type
            foreach (var attackerType in attackerTypes)
            {
                foreach (var attackType in attackTypes)
                {
                    if (attackerType.Equals(attackType, StringComparison.OrdinalIgnoreCase))
                    {
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "STAB Debug", 
                            string.Format("STAB match found: Attacker type '{0}' matches attack type '{1}'", attackerType, attackType));
                        return STAB_MULTIPLIER;
                    }
                }
            }
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "STAB Debug", 
                string.Format("No STAB: Attacker types [{0}] don't match attack types [{1}]", 
                string.Join(", ", attackerTypes), string.Join(", ", attackTypes)));
            return 1.0f;
        }
        
        /// <summary>
        /// Extract type tags from a character's management tags
        /// </summary>
        /// <param name="character">Character to check</param>
        /// <returns>List of type tags (e.g., ["fire", "steel"])</returns>
        private static List<string> ExtractTypeTagsFromCharacter(BattleCharacterBase character)
        {
            var types = new List<string>();
            
            if (character is BattleEnemyData enemy && enemy.monster?.tags != null)
            {
                // Check monster's management tags for type tags
                var tags = enemy.monster.tags.ToLowerInvariant();
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Dual Type Debug", 
                    string.Format("Enemy {0} management tags: '{1}'", character.Name, tags));
                types.AddRange(ExtractTypesFromTagString(tags));
            }
            else if (character is BattlePlayerData player && player.player?.rom?.tags != null)
            {
                // Check player character's management tags for type tags
                var tags = player.player.rom.tags.ToLowerInvariant();
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Dual Type Debug", 
                    string.Format("Player {0} management tags: '{1}'", character.Name, tags));
                types.AddRange(ExtractTypesFromTagString(tags));
            }
            else
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Dual Type Debug", 
                    string.Format("Character {0} has no management tags", character.Name));
            }
            
            return types;
        }
        
        /// <summary>
        /// Extract type names from a tag string
        /// </summary>
        /// <param name="tagString">Tag string to parse</param>
        /// <returns>List of type names</returns>
        private static List<string> ExtractTypesFromTagString(string tagString)
        {
            var types = new List<string>();
            var commonTypes = new[] { "fire", "water", "grass", "electric", "ice", "fighting", "poison", 
                                    "ground", "flying", "wind", "psychic", "bug", "rock", "ghost", "dragon", 
                                    "dark", "steel", "fairy", "normal" };
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TAG PARSE", 
                string.Format("🔍 Parsing tag string: '{0}'", tagString));
            
            foreach (var type in commonTypes)
            {
                string searchTag = "#" + type;
                if (tagString.Contains(searchTag))
                {
                    types.Add(type);
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TAG PARSE", 
                        string.Format("✅ Found type tag: {0}", searchTag));
                }
            }
            
            if (types.Count == 0)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "TAG PARSE", 
                    "❌ No type tags found in string");
            }
            
            return types;
        }
        
        /// <summary>
        /// Extract attack types from skill tags (e.g., #fire, #water, #fire #wind)
        /// </summary>
        /// <param name="attacker">The attacking character</param>
        /// <returns>List of type names (e.g., ["fire", "wind"])</returns>
        private static List<string> ExtractTypesFromSkill(BattleCharacterBase attacker)
        {
            var types = new List<string>();
            
            if (attacker?.selectedSkill?.tags == null) 
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Dual Type Debug", 
                    string.Format("Character {0} has no skill or skill has no tags", attacker?.Name ?? "null"));
                return types;
            }
            
            var tags = attacker.selectedSkill.tags.ToLowerInvariant();
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Dual Type Debug", 
                string.Format("Skill '{0}' tags: '{1}'", attacker.selectedSkill.name, tags));
            
            types.AddRange(ExtractTypesFromTagString(tags));
            
            if (types.Count > 0)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Dual Type Debug", 
                    string.Format("Found attack types: [{0}]", string.Join(", ", types)));
            }
            else
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Dual Type Debug", 
                    "No type tags found in skill");
            }
            
            return types;
        }
        
        /// <summary>
        /// Get type effectiveness multiplier (Complete Pokemon Gen 6+ type chart)
        /// Based on official Pokemon type effectiveness chart from pokemondb.net
        /// </summary>
        /// <param name="attackType">Attacking type</param>
        /// <param name="defenseType">Defending type</param>
        /// <returns>Damage multiplier (0.0f = no effect, 0.5f = not very effective, 1.0f = normal, 2.0f = super effective)</returns>
        private static float GetTypeEffectiveness(string attackType, string defenseType)
        {
            // Complete Pokemon-style type effectiveness chart (Gen 6+)
            // Source: https://pokemondb.net/type
            
            switch (attackType.ToLowerInvariant())
            {
                case "normal":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "rock": case "steel": return 0.5f; // Not very effective
                        case "ghost": return 0.0f; // No effect
                        default: return 1.0f; // Normal damage
                    }
                    
                case "fire":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "grass": case "ice": case "bug": case "steel": return 2.0f; // Super effective
                        case "fire": case "water": case "rock": case "dragon": return 0.5f; // Not very effective
                        default: return 1.0f; // Normal damage
                    }
                    
                case "water":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "fire": case "ground": case "rock": return 2.0f; // Super effective
                        case "water": case "grass": case "dragon": return 0.5f; // Not very effective
                        default: return 1.0f; // Normal damage
                    }
                    
                case "electric":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "water": case "flying": return 2.0f; // Super effective
                        case "electric": case "grass": case "dragon": return 0.5f; // Not very effective
                        case "ground": return 0.0f; // No effect
                        default: return 1.0f; // Normal damage
                    }
                    
                case "grass":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "water": case "ground": case "rock": return 2.0f; // Super effective
                        case "fire": case "grass": case "poison": case "flying": case "bug": case "dragon": case "steel": return 0.5f; // Not very effective
                        default: return 1.0f; // Normal damage
                    }
                    
                case "ice":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "grass": case "ground": case "flying": case "dragon": return 2.0f; // Super effective
                        case "fire": case "water": case "ice": case "steel": return 0.5f; // Not very effective
                        default: return 1.0f; // Normal damage
                    }
                    
                case "fighting":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "normal": case "ice": case "rock": case "dark": case "steel": return 2.0f; // Super effective
                        case "poison": case "flying": case "psychic": case "bug": case "fairy": return 0.5f; // Not very effective
                        case "ghost": return 0.0f; // No effect
                        default: return 1.0f; // Normal damage
                    }
                    
                case "poison":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "grass": case "fairy": return 2.0f; // Super effective
                        case "poison": case "ground": case "rock": case "ghost": return 0.5f; // Not very effective
                        case "steel": return 0.0f; // No effect
                        default: return 1.0f; // Normal damage
                    }
                    
                case "ground":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "fire": case "electric": case "poison": case "rock": case "steel": return 2.0f; // Super effective
                        case "grass": case "bug": return 0.5f; // Not very effective
                        case "flying": return 0.0f; // No effect
                        default: return 1.0f; // Normal damage
                    }
                    
                case "flying":
                case "wind":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "grass": case "fighting": case "bug": return 2.0f; // Super effective
                        case "electric": case "rock": case "steel": return 0.5f; // Not very effective
                        default: return 1.0f; // Normal damage
                    }
                    
                case "psychic":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "fighting": case "poison": return 2.0f; // Super effective
                        case "psychic": case "steel": return 0.5f; // Not very effective
                        case "dark": return 0.0f; // No effect
                        default: return 1.0f; // Normal damage
                    }
                    
                case "bug":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "grass": case "psychic": case "dark": return 2.0f; // Super effective
                        case "fire": case "fighting": case "poison": case "flying": case "ghost": case "steel": case "fairy": return 0.5f; // Not very effective
                        default: return 1.0f; // Normal damage
                    }
                    
                case "rock":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "fire": case "ice": case "flying": case "bug": return 2.0f; // Super effective
                        case "fighting": case "ground": case "steel": return 0.5f; // Not very effective
                        default: return 1.0f; // Normal damage
                    }
                    
                case "ghost":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "psychic": case "ghost": return 2.0f; // Super effective
                        case "dark": return 0.5f; // Not very effective
                        case "normal": return 0.0f; // No effect
                        default: return 1.0f; // Normal damage
                    }
                    
                case "dragon":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "dragon": return 2.0f; // Super effective
                        case "steel": return 0.5f; // Not very effective
                        case "fairy": return 0.0f; // No effect
                        default: return 1.0f; // Normal damage
                    }
                    
                case "dark":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "psychic": case "ghost": return 2.0f; // Super effective
                        case "fighting": case "dark": case "fairy": return 0.5f; // Not very effective
                        default: return 1.0f; // Normal damage
                    }
                    
                case "steel":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "ice": case "rock": case "fairy": return 2.0f; // Super effective
                        case "fire": case "water": case "electric": case "steel": return 0.5f; // Not very effective
                        default: return 1.0f; // Normal damage
                    }
                    
                case "fairy":
                    switch (defenseType.ToLowerInvariant())
                    {
                        case "fighting": case "dragon": case "dark": return 2.0f; // Super effective
                        case "fire": case "poison": case "steel": return 0.5f; // Not very effective
                        default: return 1.0f; // Normal damage
                    }
                    
                default:
                    return 1.0f; // Normal damage for unknown types
            }
        }
        
        /// <summary>
        /// Retarget all pending attacks from one character to another
        /// This is called when a character switches out and needs to redirect incoming attacks
        /// </summary>
        /// <param name="fromCharacter">The character that's switching out</param>
        /// <param name="toCharacter">The character that's switching in</param>
        private void RetargetAllAttacksFromTo(BattleCharacterBase fromCharacter, BattleCharacterBase toCharacter)
        {
            if (fromCharacter == null || toCharacter == null) return;
            
            int retargetCount = 0;
            
            // Check all characters in the action queue who haven't acted yet
            foreach (var actionEntry in battleEntryCharacters)
            {
                var attacker = actionEntry.character;
                if (attacker == null || attacker.targetCharacter == null) continue;
                
                // Check if this character is targeting the outgoing character
                for (int i = 0; i < attacker.targetCharacter.Length; i++)
                {
                    if (attacker.targetCharacter[i] == fromCharacter)
                    {
                        // Retarget to the incoming character
                        attacker.targetCharacter[i] = toCharacter;
                        retargetCount++;
                        
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Retarget", 
                            string.Format("{0}'s attack retargeted from {1} to {2}", 
                            attacker.Name, fromCharacter.Name, toCharacter.Name));
                    }
                }
            }
            
            // Also check all active characters who might have pending actions
            var allActiveCharacters = createBattleCharacterList();
            foreach (var character in allActiveCharacters)
            {
                if (character == null || character.targetCharacter == null) continue;
                if (battleEntryCharacters.Any(entry => entry.character == character)) continue; // Skip if already in queue
                
                // Check if this character is targeting the outgoing character
                for (int i = 0; i < character.targetCharacter.Length; i++)
                {
                    if (character.targetCharacter[i] == fromCharacter)
                    {
                        // Retarget to the incoming character
                        character.targetCharacter[i] = toCharacter;
                        retargetCount++;
                        
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Retarget", 
                            string.Format("{0}'s pending action retargeted from {1} to {2}", 
                            character.Name, fromCharacter.Name, toCharacter.Name));
                    }
                }
            }
            
            if (retargetCount > 0)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Retarget", 
                    string.Format("Successfully retargeted {0} attack(s) from {1} to {2}", 
                    retargetCount, fromCharacter.Name, toCharacter.Name));
            }
            else
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Retarget", 
                    string.Format("No attacks needed retargeting from {0} to {1}", 
                    fromCharacter.Name, toCharacter.Name));
            }
        }
        
        /// <summary>
        /// Clear all status ailments with #switchclear tag from a character
        /// This is called when a character switches out of battle
        /// </summary>
        /// <param name="character">The character to clear switch-clearable statuses from</param>
        private void ClearSwitchClearableStatuses(BattleCharacterBase character)
        {
            if (character?.conditionInfoDic == null) 
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                    string.Format("Character {0} has no condition dictionary", character?.Name ?? "NULL"));
                return;
            }
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                string.Format("Checking {0} conditions on {1} for #switchclear tags", 
                character.conditionInfoDic.Count, character.Name));
            
            var conditionsToRemove = new List<AttackAttributeType>();
            
            foreach (var conditionEntry in character.conditionInfoDic)
            {
                var condition = conditionEntry.Value.rom;
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                    string.Format("Examining condition '{0}' on {1}", condition?.name ?? "NULL", character.Name));
                
                bool hasTag = HasSwitchClearTag(condition);
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                    string.Format("Tag check result for '{0}': {1}", condition?.name ?? "NULL", hasTag));
                
                if (hasTag)
                {
                    conditionsToRemove.Add(conditionEntry.Key);
                    
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear", 
                        string.Format("FOUND #switchclear status '{0}' on {1} - marking for removal", 
                        condition.name, character.Name));
                }
                else
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                        string.Format("Condition '{0}' does NOT have #switchclear tag", condition?.name ?? "NULL"));
                }
            }
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                string.Format("Found {0} conditions to remove from {1}", conditionsToRemove.Count, character.Name));
            
            // Remove all conditions marked for clearing
            foreach (var conditionKey in conditionsToRemove)
            {
                character.conditionInfoDic.Remove(conditionKey);
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear", 
                    string.Format("Removed condition key {0} from {1}", conditionKey, character.Name));
            }
            
            // Refresh condition effects after removal
            if (conditionsToRemove.Count > 0)
            {
                // Refresh condition effects on the underlying game data
                if (character is BattlePlayerData playerData)
                {
                    playerData.player.refreshConditionEffect();
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear", 
                        string.Format("Refreshed condition effects for player {0}", character.Name));
                }
                // Note: BattleEnemyData doesn't need refreshConditionEffect as it doesn't persist after battle
                
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear", 
                    string.Format("SUCCESS: Cleared {0} #switchclear status(es) from {1}", 
                    conditionsToRemove.Count, character.Name));
            }
            else
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Switch Clear Debug", 
                    string.Format("No #switchclear conditions found on {0}", character.Name));
            }
        }

        /// <summary>
        /// Turn-based planning system - all sides select moves before execution
        /// </summary>
        private void UpdateBattleState_WaitCtbGauge()
        {
            // 即時行動がセットされていたら強制的にターン発動
            // If immediate action is set, the turn will be forced.
            if (battleEntryCharacters.Count > 0)
            {
                activeCharacter = battleEntryCharacters[0].character;
                commandSelectPlayer = activeCharacter as ExBattlePlayerData;
                activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.NONE;
                battleEntryCharacters[0].setter?.Invoke();// 強制行動セットがあるかもしれない) / There may be a forced action set.)
                battleEvents.start(Rom.Script.Trigger.BATTLE_TURN);
                activeCharacter.InitializeBattleCommandDisabled(catalog);
                battleEntryCharacters.RemoveAt(0);
                ChangeBattleState(BattleState.ReadyExecuteCommand);
                return;
            }

            // TURN-BASED PLANNING: Reset all command selections for new turn
            ResetAllCommandSelections();
            
            // Start with player command selection phase
            StartPlayerCommandSelectionPhase();
        }
        
        /// <summary>
        /// Reset all character command selections for a new turn
        /// </summary>
        private void ResetAllCommandSelections()
        {
            // Reset all command selections for new turn
            foreach (var player in playerData.Where(p => !p.IsDeadCondition()))
            {
                player.selectedBattleCommandType = BattleCommandType.Undecided;
                player.forceSetCommand = false;
            }
            
            foreach (var enemy in enemyData.Where(e => !e.IsDeadCondition()))
            {
                enemy.selectedBattleCommandType = BattleCommandType.Undecided;
            }
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Turn Planning", 
                "New turn started - all characters must select actions before execution");
        }
        
        /// <summary>
        /// Start the player command selection phase
        /// </summary>
        private void StartPlayerCommandSelectionPhase()
        {
            var alivePlayerCount = playerData.Count(p => !p.IsDeadCondition());
            
            if (alivePlayerCount == 0)
            {
                // No alive players, skip to enemy phase
                StartEnemyCommandSelectionPhase();
                return;
            }
            
            // Reset command selection counters
            commandSelectedMemberCount = 0;
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Turn Planning", 
                string.Format("Player command selection phase started - {0} players need to select actions", alivePlayerCount));
            
            // Start with first alive player
            ChangeBattleState(BattleState.SelectActivePlayer);
        }
        
        /// <summary>
        /// Start the enemy command selection phase
        /// </summary>
        private void StartEnemyCommandSelectionPhase()
        {
            var aliveEnemyCount = enemyData.Count(e => !e.IsDeadCondition());
            
            if (aliveEnemyCount == 0)
            {
                // No alive enemies, proceed to execution phase
                StartActionExecutionPhase();
                return;
            }
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Turn Planning", 
                string.Format("Enemy command selection phase started - {0} enemies selecting actions", aliveEnemyCount));
            
            // Auto-select actions for all alive enemies
            foreach (var enemy in enemyData.Where(e => !e.IsDeadCondition()))
            {
                SelectEnemyCommand(enemy, false, false);
            }
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Turn Planning", 
                string.Format("All {0} enemies have selected their actions", aliveEnemyCount));
            
            // Proceed to action execution phase
            StartActionExecutionPhase();
        }
        
        /// <summary>
        /// Start the action execution phase where all selected actions are executed in priority/speed order
        /// </summary>
        private void StartActionExecutionPhase()
        {
            var totalPlayers = playerData.Count(p => !p.IsDeadCondition());
            var totalEnemies = enemyData.Count(e => !e.IsDeadCondition());
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Turn Planning", 
                string.Format("All actions selected! Starting execution phase - Players: {0}, Enemies: {1}", 
                totalPlayers, totalEnemies));
            
            // Transition to action sorting and execution
            ChangeBattleState(BattleState.SortBattleActions);
        }

        /// <summary>
        /// 指定した順目でターンが回ってくるキャラを取得する
        /// Get a character whose turn comes around in the specified order
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        internal BattleCharacterBase GetTurnOrdererdCharacter(int index)
        {
            var range = Enumerable.Range(1, index + 1);

            // indexターン先までを配列化する
            // Array up to index turn destination
            return range.SelectMany(x => playerData.Select(y => { return new { data = (BattleCharacterBase)y, turn = ((ExBattlePlayerData)y).GetNormalizedTurn(x) }; }))
                .Concat(range.SelectMany(x => enemyData.Select(y => { return new { data = (BattleCharacterBase)y, turn = ((ExBattleEnemyData)y).GetNormalizedTurn(x) }; })))
                .OrderBy(x => x.turn)
                .ElementAtOrDefault(index)?.data;
        }

        private void UpdateBattleState_SetEnemyBattleCommand()
        {
            if (owner.mapScene.isToastVisible() || !battleViewer.IsEffectEndPlay)
            {
                return;
            }

            SelectEnemyCommand(activeCharacter as BattleEnemyData, false, false);
            ((BattleViewer3D)battleViewer).setEnemyActionReady(activeCharacter as BattleEnemyData);
            //foreach (var monsterData in enemyData)
            //{
            //    SelectEnemyCommand(monsterData, false, false);
            //    ((BattleViewer3D)battleViewer).setEnemyActionReady(monsterData);
            //}

            // エフェクト先読み
            // Effect lookahead
            GetCommandEffectImpl(activeCharacter, catalog, out var efA, out var efB);
            preloadEffect(efA);
            preloadEffect(efB);
            ChangeBattleState(BattleState.ReadyExecuteCommand);
            //ChangeBattleState(BattleState.SelectActivePlayer);
        }

        /// <summary>
        /// 敵のコマンドを選択する
        /// Select Enemy Command
        /// </summary>
        /// <param name="monsterData">コマンドを決定する敵</param>
        /// <param name="monsterData">Enemy to determine command</param>
        /// <param name="isContinous">連続行動かどうか</param>
        /// <param name="isContinous">continuous action or not</param>
        /// <param name="isCounter">カウンターかどうか</param>
        /// <param name="isCounter">counter or not</param>
        private void SelectEnemyCommand(BattleEnemyData monsterData, bool isContinous, bool isCounter,
            int turn = -1, IEnumerable<Rom.ActionInfo> activeAction = null)
        {
            monsterData.counterAction = BattleEnemyData.CounterState.NONE;
            monsterData.selectedBattleCommandTags = null;

            if (monsterData.selectedBattleCommandType != BattleCommandType.Undecided)
            {
                // イベントパネルから行動が強制指定されている場合にここに来る
                // Comes here if an action is forced from the event panel
            }
            else if (monsterData.HitPoint > 0)
            {
                // Pokemon-style: Check if enemy should switch
                if (stockEnemyData.Count > 0 && ShouldEnemySwitch(monsterData))
                {
                    monsterData.selectedBattleCommandType = BattleCommandType.Position;
                    
                    // Select a random stock enemy to switch with
                    var switchTarget = stockEnemyData[battleRandom.Next(stockEnemyData.Count)];
                    monsterData.targetCharacter = new BattleCharacterBase[] { switchTarget };
                    
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, monsterData.Name,
                        string.Format("Enemy decides to switch with {0}", switchTarget.Name));
                    return;
                }
                if (!isCounter && !isContinous)
                {
                    // 強化能力 更新
                    // Enhanced Ability Update
                    UpdateEnhanceEffect(monsterData.attackEnhanceEffects);
                    UpdateEnhanceEffect(monsterData.guardEnhanceEffects);
                }

                if (activeAction == null)
                    activeAction = GetActiveActions(monsterData, isCounter, turn);

                activeAction = SelectEnabledActiveActions(monsterData, activeAction);

                if (activeAction.Count() > 0)
                {
                    var executeAction = activeAction.ElementAt(battleRandom.Next(activeAction.Count()));

                    foreach (var action in activeAction)
                    {
                        if (action == executeAction)
                            continue;

                        if (action.type == Rom.ActionType.SKILL)
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, monsterData.Name,
                                string.Format("Selectable Action / Skill : {0}", catalog.getItemFromGuid(action.refByAction)?.name ?? "Nothing"));
                        else
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, monsterData.Name,
                                string.Format("Selectable Action / Type : {0}", action.type.ToString()));
                    }

                    if (executeAction.type == Rom.ActionType.SKILL)
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, monsterData.Name,
                            string.Format("Choiced Action / Skill : {0}", catalog.getItemFromGuid(executeAction.refByAction)?.name ?? "Nothing"));
                    else
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, monsterData.Name,
                            string.Format("Choiced Action / Type : {0}", executeAction.type.ToString()));

                    monsterData.commandTargetList.Clear();
                    battleViewer.commandTargetSelector.Clear();

                    monsterData.continuousAction = executeAction.continuous;
                    if (!monsterData.continuousAction)
                        monsterData.alreadyExecuteActions.Clear();

                    // 同じ行は2回まで
                    // Same line up to 2 times
                    if (monsterData.alreadyExecuteActions.Contains(executeAction))
                    {
                        monsterData.continuousAction = false;
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, monsterData.Name,
                            string.Format("Cancel Action By Already Executed.", executeAction.type.ToString()));
                    }
                    else
                    {
                        monsterData.alreadyExecuteActions.Add(executeAction);
                    }

                    if (!isCounter && !isContinous)
                        monsterData.currentActionTurn = monsterData.ExecuteCommandTurnCount;

                    monsterData.selectedBattleCommandTags = executeAction.tags + " " + Rom.EffectParamSettings.GetTag(executeAction.type);

                    switch (executeAction.type)
                    {
                        case Rom.ActionType.ATTACK:
                        case Rom.ActionType.CRITICAL:
                        case Rom.ActionType.FORCE_CRITICAL:
                            foreach (var player in targetPlayerData.Where(target => (target.HitPoint > 0) && IsHitRange(monsterData, target)))
                            {
                                monsterData.commandTargetList.Add(player);
                            }

                            if (monsterData.commandTargetList.Count == 0)
                            {
                                goto case Rom.ActionType.DO_NOTHING;
                            }

                            switch (executeAction.type)
                            {
                                case Rom.ActionType.ATTACK:
                                    monsterData.selectedBattleCommandType = BattleCommandType.Attack;
                                    break;
                                case Rom.ActionType.CRITICAL:
                                    monsterData.selectedBattleCommandType = BattleCommandType.Critical;
                                    break;
                                case Rom.ActionType.FORCE_CRITICAL:
                                    monsterData.selectedBattleCommandType = BattleCommandType.ForceCritical;
                                    break;
                                default:
                                    break;
                            }
                            break;

                        case Rom.ActionType.SKILL:
                            var skill = (Rom.NSkill)catalog.getItemFromGuid(executeAction.refByAction);

                            // スキルが無かったら何もしないよう修正
                            // Fixed to do nothing if there is no skill
                            if (skill == null)
                            {
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, monsterData.Name,
                                    string.Format("Cancel Action By Missing Skill.", executeAction.type.ToString()));
                                monsterData.selectedBattleCommandType = BattleCommandType.Nothing;
                                break;
                            }

                            monsterData.selectedItem = null;
                            monsterData.selectedSkill = skill;
                            monsterData.selectedBattleCommandType = BattleCommandType.Skill;

                            switch (monsterData.selectedSkill.option.target)
                            {
                                case Rom.TargetType.PARTY_ONE:
                                case Rom.TargetType.PARTY_ONE_ENEMY_ALL:

                                    foreach (var enemy in targetEnemyData)
                                    {
                                        monsterData.commandTargetList.Add(enemy);
                                    }

                                    break;

                                case Rom.TargetType.ENEMY_ONE:
                                case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                                case Rom.TargetType.SELF_ENEMY_ONE:
                                case Rom.TargetType.OTHERS_ENEMY_ONE:
                                    foreach (var player in targetPlayerData.Where(player => player.HitPoint > 0))
                                    {
                                        monsterData.commandTargetList.Add(player);
                                    }

                                    break;
                            }
                            break;

                        case Rom.ActionType.CHARGE:
                            monsterData.selectedBattleCommand = new BattleCommand();
                            monsterData.selectedBattleCommand.power = executeAction.option;
                            monsterData.selectedBattleCommandType = BattleCommandType.Charge;
                            break;

                        case Rom.ActionType.GUARD:
                            monsterData.selectedBattleCommand = new BattleCommand();
                            monsterData.selectedBattleCommand.power = executeAction.option;
                            monsterData.selectedBattleCommandType = BattleCommandType.Guard;
                            break;

                        case Rom.ActionType.ESCAPE:
                            monsterData.selectedBattleCommandType = BattleCommandType.MonsterEscape;
                            break;

                        case Rom.ActionType.DO_NOTHING:
                            monsterData.selectedBattleCommandType = BattleCommandType.Nothing;
                            break;
                    }

                    if (monsterData.commandTargetList.Count > 0)
                    {
                        battleViewer.commandTargetSelector.AddBattleCharacters(monsterData.commandTargetList);
                        var result = TargetSelectWithHateRate(monsterData.commandTargetList, monsterData);
                        battleViewer.commandTargetSelector.SetSelect(result);
                    }
                }
                else
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, monsterData.Name, "No Selectable Action");

                    monsterData.continuousAction = false;
                    monsterData.selectedBattleCommandType = BattleCommandType.Nothing;
                }

                monsterData.targetCharacter = GetTargetCharacters(monsterData);

                // ターン指定でなければモンスターのアクションを一つ進める
                // Advance the monster's action by one if it is not a turn designation
                if (turn < 0)
                {
                    monsterData.ExecuteCommandTurnCount++;

                    if (monsterData.monster.battleActions.Count == 0 || monsterData.ExecuteCommandTurnCount > monsterData.monster.battleActions.Max(act => act.turn))
                    {
                        monsterData.ExecuteCommandTurnCount = 1;
                    }
                }
            }
            else
            {
                monsterData.continuousAction = false;
                monsterData.selectedBattleCommandType = BattleCommandType.Nothing_Down;
            }
        }

        /// <summary>
        /// 無効化されているアクションを外した一覧を取得する
        /// Get a list of disabled actions
        /// </summary>
        /// <param name="monsterData"></param>
        /// <param name="activeAction"></param>
        /// <returns></returns>
		private IEnumerable<Rom.ActionInfo> SelectEnabledActiveActions(BattleEnemyData monsterData, IEnumerable<Rom.ActionInfo> activeAction)
		{
            var list = activeAction.ToList();

            foreach (var tag in monsterData.GetBattleCommandDisabledTargetTagList())
            {
                var filterTagProperties = ItemFilter.CreateFilteringTagFromText(tag);

                for (int i = list.Count - 1; i >= 0; i--)
				{
                    if (ItemFilter.FilterCommand(list[i], filterTagProperties))
                    {
                        list.RemoveAt(i);
                    }
                }
            }

			return list;
		}

		/// <summary>
		/// モンスターの現在のターンで有効なアクションの一覧を取得する
		/// Get a list of valid actions for the monster's current turn
		/// </summary>
		/// <param name="monsterData"></param>
		/// <param name="isContinous"></param>
		/// <param name="isCounter"></param>
		/// <returns></returns>
		private IEnumerable<Rom.ActionInfo> GetActiveActions(BattleEnemyData monsterData, bool isCounter, int turn = -1)
        {
            int hitPointRate = (int)(monsterData.HitPointPercent * 100);
            int magicPointRate = (int)(monsterData.MagicPointPercent * 100);

            turn = turn > 0 ? turn : monsterData.ExecuteCommandTurnCount;

            if (isCounter)
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, monsterData.Name,
                    string.Format("Select Counter Action / Turn No. : {0}", turn));
            else
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, monsterData.Name,
                    string.Format("Select Action / Turn No. : {0}", turn));

            var actionList = monsterData.monster.battleActions;
            var activeAction = actionList.Where(act => act.turn == turn
                && checkCondition(act, monsterData, hitPointRate, magicPointRate, isCounter));
            var removeActions = new List<Rom.ActionInfo>();

            // MPが足りないスキルを除外するための関数
            // Function for excluding skills with insufficient MP
            void filter()
            {
                switch (monsterData.monster.aiPattern)
                {
                    case Rom.AIPattern.CLEVER:
                    case Rom.AIPattern.TRICKY:
                        foreach (var act in activeAction)
                        {
                            if (act.type == Rom.ActionType.SKILL)
                            {
                                var skill = catalog.getItemFromGuid(act.refByAction) as Rom.NSkill;

                                if (skill == null)
                                {
                                    removeActions.Add(act);
                                }
                                // MPが足りない？
                                // Not enough MP?
                                else if (!IsQualifiedSkillCostStatus(monsterData, skill))
                                {
                                    removeActions.Add(act);
                                }
                                // スキル対象が全員で誰かが反射状態？
                                // Is everyone the skill target and someone else in a reflex state?
                                else if (skill.TargetE == (int)Rom.TargetType.ENEMY_ALL && monsterData.EnemyPartyRefMember.Exists(x => x.GetReflectionParam(skill, false) != null))
                                {
                                    removeActions.Add(act);
                                }
                                // スキル対象が単体だが全員が反射状態？
                                // The skill target is a single one, but are they all reflected?
                                else if (skill.TargetE == (int)Rom.TargetType.ENEMY_ONE && monsterData.EnemyPartyRefMember.All(x => x.GetReflectionParam(skill, false) != null))
                                {
                                    removeActions.Add(act);
                                }
                            }
                        }

                        activeAction = activeAction.Except(removeActions);
                        break;
                }
            }

            filter();

            // 現在のターン数で実行できる行動が1つも無ければ標準の行動(0ターンの行動)から選択する
            // If there is no action that can be executed in the current number of turns, select from standard actions (0 turn actions)
            if (activeAction.Count() == 0)
            {
                activeAction = actionList.Where(act => act.turn == 0 && checkCondition(act, monsterData, hitPointRate, magicPointRate, isCounter));

                // MPが足りないスキルを除外
                // Exclude skills with insufficient MP
                filter();
            }

            return activeAction;
        }

        private BattleCharacterBase TargetSelectWithHateRate(IEnumerable<BattleCharacterBase> origList, BattleEnemyData data = null)
        {
            var hateRateSumList = new List<int>();
            var list = origList;

            // 賢いAIのときは対象から反射状態のキャストを抜く
            // When using smart AI, remove casting in a reflective state from the target.
            if (data != null && data.selectedSkill != null && data.monster.aiPattern != Rom.AIPattern.NORMAL)
            {
                list = origList.Where(x => x.GetReflectionParam(data.selectedSkill, false) == null).ToArray();
            }

            int cnt = 0;
            foreach (var target in list)
            {
                var hateRate = Math.Max(0, target.HateCondition + 100);
                hateRateSumList.Add(hateRate + hateRateSumList.LastOrDefault());
                cnt++;
            }

            if (cnt == 0)
                return null;

            if (hateRateSumList.Last() == 0)
            {
                hateRateSumList.Clear();
                cnt = 1;
                foreach (var target in list)
                {
                    hateRateSumList.Add(cnt);
                    cnt++;
                }
            }

            switch (data?.monster.aiPattern ?? Rom.AIPattern.NORMAL)
            {
                case Rom.AIPattern.NORMAL:
                case Rom.AIPattern.CLEVER:
                {
                    var rnd = battleRandom.Next(hateRateSumList.Last());
                    return list.ElementAt(hateRateSumList.FindIndex(x => rnd < x));
                }

                case Rom.AIPattern.TRICKY:
                    if (battleRandom.Next(100) < 75)
                    {
                        // 狙えるターゲットの中で最もHPが少ないキャラクターを狙う
                        // Aim for the character with the lowest HP among all possible targets.
                        return list.OrderBy(target => target.HitPoint).ElementAt(0);
                    }
                    else
                    {
                        var rnd = battleRandom.Next(hateRateSumList.Last());
                        return list.ElementAt(hateRateSumList.FindIndex(x => rnd < x));
                    }
            }

            return null;
        }

        private bool checkCondition(Rom.ActionInfo act, BattleEnemyData monsterData, int hitPointRate, int magicPointRate, bool isCounter)
        {
            bool ok = true;
            bool containsCounter = false;
            foreach (var cond in act.conditions)
            {
                if (!ok)
                    break;

                switch (cond.type)
                {
                    case Rom.ActionConditionType.HP:
                        if (hitPointRate < cond.min || cond.max < hitPointRate)
                            ok = false;
                        break;
                    case Rom.ActionConditionType.MP:
                        if (magicPointRate < cond.min || cond.max < magicPointRate)
                            ok = false;
                        break;
                    case Rom.ActionConditionType.AVRAGE_LEVEL:
                        var average = playerData.Average(x => x.player.level);
                        if (average < cond.min || cond.max < average)
                            ok = false;
                        break;
                    case Rom.ActionConditionType.TURN:
                        if (totalTurn < cond.min || cond.max < totalTurn)
                            ok = false;
                        break;
                    case Rom.ActionConditionType.STATE:
                        if (!monsterData.conditionInfoDic.ContainsKey(cond.refByConiditon))
                            ok = false;
                        break;
                    case Rom.ActionConditionType.SWITCH:
                        if (!owner.data.system.GetSwitch(cond.option, Guid.Empty, false))
                            ok = false;
                        break;
                    case Rom.ActionConditionType.COUNTER:
                        containsCounter = true;
                        break;
                    case Rom.ActionConditionType.CONSUMPTION_STATUS:
                        {
                            var info = gameSettings.GetCastStatusParamInfo(cond.refByConiditon);

                            if (info != null)
                            {
                                var percent = monsterData.GetStatus(gameSettings, info.ConsumptionPercentId);

                                if ((percent < cond.min) || (cond.max < percent))
                                {
                                    ok = false;
                                }
                            }

                        }
                        break;
                }
            }

            if (containsCounter != isCounter)
                ok = false;

            return ok;
        }

        private void UpdateBattleState_ReadyExecuteCommand()
        {
            if (battleEvents.isBusy())
                return;
            battleEvents.clearCurrentProcessingTrigger();

            // キャストの行動の都度、位置調整する時はコメントを外す
            // Remove the comment when adjusting the position each time the cast acts
            //UpdatePosition();

            // SAFETY CHECK: Ensure activeCharacter is not null
            if (activeCharacter == null)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Error", 
                    "CRITICAL ERROR: activeCharacter is null in ReadyExecuteCommand! Returning to turn start.");
                ChangeBattleState(WAIT_CTB_GAUGE);
                return;
            }

            if (activeCharacter is BattlePlayerData pl) pl.forceSetCommand = false;
            activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.NONE;
            attackCount = 0;

            // 前回の行動がカウンターだった場合は、改めて行動をセットする
            // If the previous action was a counter, set the action again
            if (activeCharacter is BattleEnemyData)
            {
                var enm = (BattleEnemyData)activeCharacter;
                if (enm.counterAction == BattleEnemyData.CounterState.COUNTER)
                {
                    enm.counterAction = BattleEnemyData.CounterState.AFTER;
                }
                else if (enm.counterAction == BattleEnemyData.CounterState.AFTER)
                {
                    activeCharacter.selectedBattleCommandType = BattleCommandType.Undecided;
                    SelectEnemyCommand(enm, false, false, enm.currentActionTurn);
                }
            }


            if (activeCharacter.selectedBattleCommandType == BattleCommandType.Skip)
            {
                ChangeBattleState(BattleState.CheckBattleCharacterDown1);
            }
            else
            {
				// 状態異常による行動の変更
				// Behavior changes due to status ailments
                foreach (var e in activeCharacter.conditionInfoDic)
                {
                    var condition = e.Value.rom;

                    if (condition != null)
                    {
                        if (activeCharacter.selectedBattleCommandType != BattleCommandType.PlayerEscape)
                        {
                            if (condition.IsActionDisabled)
                            {
                                activeCharacter.selectedBattleCommandType = BattleCommandType.Nothing_Down;
                                if (activeCharacter is BattleEnemyData)
                                {
                                    ((BattleEnemyData)activeCharacter).continuousAction = false;
                                    ((BattleEnemyData)activeCharacter).alreadyExecuteActions.Clear();
                                }

                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, activeCharacter.Name,
                                    string.Format("Change Action By Condition / Condition : {0} / Type : {1}",
                                    condition.name, activeCharacter.selectedBattleCommandType.ToString()));
                                break;
                            }
                            else if (condition.IsAutoAttack)
                            {
                                activeCharacter.selectedBattleCommand = null;
                                activeCharacter.selectedBattleCommandTags = null;
                                activeCharacter.selectedBattleCommandType = BattleCommandType.Attack;
                                if (activeCharacter is BattleEnemyData)
                                {
                                    ((BattleEnemyData)activeCharacter).continuousAction = false;
                                    ((BattleEnemyData)activeCharacter).alreadyExecuteActions.Clear();
                                }

                                // 行動不能が優先なので、ここでは終わらない
                                // Incapacity is the priority, so it doesn't end here
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, activeCharacter.Name,
                                    string.Format("Change Action By Condition / Condition : {0} / Type : {1}",
                                    condition.name, activeCharacter.selectedBattleCommandType.ToString()));
                            }
                        }
                    }
                }

                // 無効コマンドのチェック
                // Check for invalid commands
                if (activeCharacter.IsSelectedBattleCommandDisabled)
                {
                    activeCharacter.selectedBattleCommand = null;
                    activeCharacter.selectedBattleCommandTags = null;
                    activeCharacter.selectedBattleCommandType = BattleCommandType.Nothing;
                }

                if (activeCharacter.selectedBattleCommandType == BattleCommandType.Nothing_Down || activeCharacter.selectedBattleCommandType == BattleCommandType.Cancel)
                {
                    ChangeBattleState(BattleState.BattleFinishCheck1);
                }
                else
                {
                    switch (activeCharacter.selectedBattleCommandType)
                    {
                        case BattleCommandType.PlayerEscape:
                        case BattleCommandType.MonsterEscape:
                            break;
                        default:
                            // 攻撃対象を変える必要があるかチェック
                            // Check if the attack target needs to be changed
                            var dontCancel = CheckAndDoReTarget();

                            // ターゲットが無になった場合は発動しない
                            // Does not activate if the target is empty
                            if (!dontCancel && activeCharacter.targetCharacter?.Length == 0)
                            {
                                ChangeBattleState(BattleState.BattleFinishCheck1);

                                return;
                            }
                            break;
                    }

                    battleEvents.start(Rom.Script.Trigger.BATTLE_BEFORE_ACTION);

                    displayedContinueConditions.Clear();

                    ChangeBattleState(BattleState.SetStatusMessageText);
                }
            }

            if (activeCharacter is BattleEnemyData)
            {
                var enm = (BattleEnemyData)activeCharacter;
                if (enm.counterAction == BattleEnemyData.CounterState.COUNTER)
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, activeCharacter.Name,
                        string.Format("Execute Counter Action / Type : {0}", activeCharacter.selectedBattleCommandType.ToString(), enm.currentActionTurn));
                }
                else if (enm.continuousAction)
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, activeCharacter.Name,
                        string.Format("Execute Continuous Action / Type : {0}", activeCharacter.selectedBattleCommandType.ToString(), activeCharacter.ExecuteCommandTurnCount));
                }
                else
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, activeCharacter.Name,
                        string.Format("Execute Action / Type : {0}", activeCharacter.selectedBattleCommandType.ToString(), activeCharacter.ExecuteCommandTurnCount));
                }
            }
            else
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, activeCharacter.Name,
                    string.Format("Execute Action / Type : {0}", activeCharacter.selectedBattleCommandType.ToString(), activeCharacter.ExecuteCommandTurnCount));
            }
        }

        internal void CalcHitCheckResult()
        {
            if ((battleState == BattleState.ReadyExecuteCommand ||
                battleState == BattleState.SetStatusMessageText) &&
                activeCharacter?.lastHitCheckResult == BattleCharacterBase.HitCheckResult.NONE)
            {
                switch (activeCharacter.selectedBattleCommandType)
                {
                    case BattleCommandType.Attack:
                    case BattleCommandType.Critical:
                    case BattleCommandType.ForceCritical:
                    case BattleCommandType.Miss:
                        {
                            var isMiss = activeCharacter.selectedBattleCommandType == BattleCommandType.Miss;
                            var isForceCritical = activeCharacter.selectedBattleCommandType == BattleCommandType.ForceCritical;

                            foreach (var target in activeCharacter.targetCharacter)
                            {
                                activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.MISSED;
                                if (!isMiss && (isForceCritical || IsHit(activeCharacter, target)))
                                {
                                    activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.HIT;
                                    var isCritical = isForceCritical || CheckCritical(activeCharacter, target, battleRandom);
                                    if (isCritical)
                                        activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.CRITICAL;
                                }
                            }
                        }
                        break;

                    case BattleCommandType.SameSkillEffect:
                        {
                            activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.MISSED;
                            BattleCharacterBase[] friendEffectTargets, enemyEffectTargets;
                            var skill = catalog.getItemFromGuid(activeCharacter.selectedBattleCommand.refGuid) as Rom.NSkill;
                            GetSkillTarget(skill, out friendEffectTargets, out enemyEffectTargets);

                            activeCharacter.targetCharacter = friendEffectTargets.Union(enemyEffectTargets).ToArray();
                            if (activeCharacter.targetCharacter.Count() > 0)
                            {
                                activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.HIT;
                            }
                        }
                        break;

                    case BattleCommandType.Skill:
                        {
                            activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.MISSED;
                            BattleCharacterBase[] friendEffectTargets, enemyEffectTargets;
                            GetSkillTarget(activeCharacter.selectedSkill, out friendEffectTargets, out enemyEffectTargets);

                            if (IsQualifiedSkillCostStatus(activeCharacter, activeCharacter.selectedSkill) &&
                                isQualifiedSkillCostItem(activeCharacter, activeCharacter.selectedSkill))
                            {
                                activeCharacter.targetCharacter = friendEffectTargets.Union(enemyEffectTargets).ToArray();
                                if (activeCharacter.targetCharacter.Count() > 0)
                                {
                                    activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.HIT;
                                }
                            }
                        }
                        break;

                    case BattleCommandType.Item:
                        {
                            activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.MISSED;
                            var selectedItem = activeCharacter.selectedItem;

                            if (selectedItem.item.IsExpandableWithSkill)
                            {
                                if (activeCharacter.targetCharacter != null)
                                {
                                    BattleCharacterBase[] friendEffectTargets, enemyEffectTargets;
                                    var skill = catalog.getItemFromGuid(selectedItem.item.expendableWithSkill.skill) as Common.Rom.NSkill;
                                    if (skill != null)
                                    {
                                        GetSkillTarget(skill, out friendEffectTargets, out enemyEffectTargets);
                                        activeCharacter.targetCharacter = friendEffectTargets.Union(enemyEffectTargets).ToArray();
                                        if (activeCharacter.targetCharacter.Count() > 0)
                                        {
                                            activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.HIT;
                                        }
                                    }
                                }
                            }
                            else if (selectedItem.item.IsExpandable)
                            {
                                if (activeCharacter.targetCharacter != null)
                                {
                                    foreach (var target in activeCharacter.targetCharacter)
                                    {
                                        if (IsHitRange(activeCharacter, selectedItem.item, target) && UseItem(selectedItem.item, target, null, recoveryStatusInfo))
                                        {
                                            activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.HIT;
                                        }
                                    }
                                }
                            }
                        }
                        break;
                }
            }
        }

        private void UpdateBattleState_ExecuteBattleCommand()
        {
            var gs = catalog.getGameSettings();
            var damageTextList = new List<BattleDamageTextInfo>();

            // 攻撃対象を変える必要があるかチェック TODO:元はこの位置で処理していたので、他に影響が出ていないか要確認
            // Check if it is necessary to change the attack target TODO: Originally it was processed at this position, so it is necessary to check if there is any other influence
            //CheckAndDoReTarget();

            activeCharacter.commandFriendEffectCharacters.Clear();
            activeCharacter.commandEnemyEffectCharacters.Clear();

            recoveryStatusInfo.Clear();

            // 強制攻撃の時はターゲットを強制的に変更する
            // Forcibly change the target during a forced attack
            switch (activeCharacter.selectedBattleCommandType)
            {
                case BattleCommandType.Attack:
                case BattleCommandType.Critical:
                case BattleCommandType.ForceCritical:
                case BattleCommandType.Miss:
                    if (attackCount == 0)
                    {
                        var attackConditionTarget = GetAttackConditionTargetCharacter(activeCharacter);

                        if (attackConditionTarget != null)
                        {
                            activeCharacter.targetCharacter = new[] { attackConditionTarget };
                        }
                    }
                    break;
                default:
                    break;
            }

            attackCount++;

            switch (activeCharacter.selectedBattleCommandType)
            {
                case BattleCommandType.Attack:
                case BattleCommandType.Critical:
                case BattleCommandType.ForceCritical:
                case BattleCommandType.Miss:
                    {
                        var isMiss = activeCharacter.selectedBattleCommandType == BattleCommandType.Miss;
                        var isForceCritical = activeCharacter.selectedBattleCommandType == BattleCommandType.ForceCritical;

                        foreach (var target in activeCharacter.targetCharacter)
                        {
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, activeCharacter.Name,
                                string.Format("Hit Judgment / Dexterity : {0} / Evation : {1}", activeCharacter.Dexterity, target.Evasion));

                            // 事前に取得している場合など、すでに確定している場合がある
                            // In some cases, it may already be confirmed, such as if it has been obtained in advance.
                            var result = activeCharacter.lastHitCheckResult;

                            activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.MISSED;
                            var isHit = !isMiss && (isForceCritical || IsHit(activeCharacter, target));
                            if (result == BattleCharacterBase.HitCheckResult.HIT || result == BattleCharacterBase.HitCheckResult.CRITICAL ||
                                (result == BattleCharacterBase.HitCheckResult.NONE && isHit))
                            {
                                activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.HIT;
                                var isCritical = isForceCritical || CheckCritical(activeCharacter, target, battleRandom);
                                isCritical = result == BattleCharacterBase.HitCheckResult.CRITICAL ||
                                    (result == BattleCharacterBase.HitCheckResult.NONE && isCritical);

                                if (isCritical)
                                    activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.CRITICAL;

                                var damage = CalcAttackWithWeaponDamage(activeCharacter, target, activeCharacter.AttackAttribute, isCritical, damageTextList);

                                target.HitPoint -= damage;
                                target.consumptionStatusValue.SubStatus(gs.maxHPStatusID, damage);

                                CheckDamageRecovery(target, damage);

                                setAttributeWithWeaponDamage(target, activeCharacter.AttackCondition, activeCharacter.ElementAttack);

                                target.CommandReactionType = ReactionType.Damage;

                                activeCharacter.commandEnemyEffectCharacters.Add(target);

                                SetCounterAction(target, activeCharacter);
                            }
                            else
                            {
                                AddBattleMissTextInfo(damageTextList, target);
                                target.CommandReactionType = ReactionType.None;
                            }
                        }
                    }
                    break;

                case BattleCommandType.Guard:
                    foreach (var target in activeCharacter.targetCharacter)
                    {
                        target.CommandReactionType = ReactionType.None;

                        // 次の自分のターンが回ってくるまでダメージを軽減
                        // Reduce damage until your next turn comes around
                        // 問題 : 素早さのパラメータが低いと後攻ガードになってしまいガードの意味が無くなってしまう
                        // Problem : If the quickness parameter is low, it will become the second guard and the meaning of guarding will be lost.
                        // 解決案 : ガードコマンドを選択した次のターンまでガードを有効にする (実質2ターンの効果に変更する)
                        // Solution: Keep the guard active until the next turn after selecting the guard command (effectively change the effect to 2 turns)
                        // TODO : 軽減できるのは物理ダメージだけ? 魔法ダメージはどう扱うのか確認する
                        // TODO: Only physical damage can be reduced? Check how magic damage is handled
                        target.guardEnhanceEffects.Add(new EnhanceEffect(activeCharacter.selectedBattleCommand.power, 1));
                    }
                    break;

                case BattleCommandType.Charge:
                    foreach (var target in activeCharacter.targetCharacter)
                    {
                        target.CommandReactionType = ReactionType.None;
                        target.attackEnhanceEffects.Add(new EnhanceEffect(activeCharacter.selectedBattleCommand.power, 2));
                    }
                    break;

                case BattleCommandType.SameSkillEffect:
                    {
                        activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.MISSED;
                        BattleCharacterBase[] friendEffectTargets, enemyEffectTargets;
                        BattleCharacterBase[] friendEffectedCharacters, enemyEffectedCharacters;

                        var skill = catalog.getItemFromGuid(activeCharacter.selectedBattleCommand.refGuid) as Rom.NSkill;

                        GetSkillTarget(skill, out friendEffectTargets, out enemyEffectTargets);



                        EffectSkill(activeCharacter, skill, friendEffectTargets.ToArray(), enemyEffectTargets.ToArray(), damageTextList, recoveryStatusInfo,
                            out friendEffectedCharacters, out enemyEffectedCharacters, out reflections, true);

                        activeCharacter.targetCharacter = friendEffectedCharacters.Union(enemyEffectedCharacters).Union(reflections.Select(x => x.target)).ToArray();
                        BattleEventControllerBase.lastSkillTargetType = (int)(skill?.option.target ?? Rom.TargetType.NONE);
                        battleEvents.setLastSkillTargetIndex(friendEffectTargets, enemyEffectTargets);

                        activeCharacter.commandFriendEffectCharacters.AddRange(friendEffectedCharacters);
                        activeCharacter.commandEnemyEffectCharacters.AddRange(enemyEffectedCharacters);

                        if (activeCharacter.targetCharacter.Count() == 0)
                        {
                            activeCharacter.targetCharacter = null;
                        }
                        else
                        {
                            activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.HIT;
                        }
                    }
                    break;

                case BattleCommandType.Skill:
                    // スキル効果対象 再選択
                    // Skill effect target reselection
                    {
                        activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.MISSED;
                        BattleCharacterBase[] friendEffectTargets, enemyEffectTargets;
                        BattleCharacterBase[] friendEffectedCharacters, enemyEffectedCharacters;

                        GetSkillTarget(activeCharacter.selectedSkill, out friendEffectTargets, out enemyEffectTargets);
                        BattleEventControllerBase.lastSkillTargetType = (int)(activeCharacter.selectedSkill?.option.target ?? Rom.TargetType.NONE);

                        if (IsQualifiedSkillCostStatus(activeCharacter, activeCharacter.selectedSkill) &&
                            isQualifiedSkillCostItem(activeCharacter, activeCharacter.selectedSkill))
                        {
                            PaySkillCost(activeCharacter, activeCharacter.selectedSkill);
                            EffectSkill(activeCharacter, activeCharacter.selectedSkill, friendEffectTargets.ToArray(), enemyEffectTargets.ToArray(), damageTextList, recoveryStatusInfo,
                                out friendEffectedCharacters, out enemyEffectedCharacters, out reflections, true);

                            activeCharacter.targetCharacter = friendEffectedCharacters.Union(enemyEffectedCharacters).Union(reflections.Select(x => x.target)).ToArray();

                            battleEvents.setLastSkillTargetIndex(friendEffectTargets, enemyEffectTargets);

                            activeCharacter.commandFriendEffectCharacters.AddRange(friendEffectedCharacters);
                            activeCharacter.commandEnemyEffectCharacters.AddRange(enemyEffectedCharacters);

                            if (activeCharacter.targetCharacter.Count() > 0)
                            {
                                activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.HIT;
                            }
                        }
                        else
                        {
                            activeCharacter.targetCharacter = null;

                            string statusName;

                            if (!IsQualifiedSkillCostStatus(activeCharacter, activeCharacter.selectedSkill, out statusName))
                            {
                                activeCharacter.skillFailCauses = statusName;
                            }
                            else if (!isQualifiedSkillCostItem(activeCharacter, activeCharacter.selectedSkill))
                                activeCharacter.skillFailCauses = catalog.getItemFromGuid(activeCharacter.selectedSkill.ConsumptionItem)?.name ?? gameSettings.glossary.item;
                        }
                    }
                    break;

                case BattleCommandType.Item:
                    {
                        activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.MISSED;
                        var selectedItem = activeCharacter.selectedItem;

                        if (selectedItem.item.IsExpandableWithSkill)
                        {
                            if (activeCharacter.targetCharacter != null)
                            {
                                BattleCharacterBase[] friendEffectTargets, enemyEffectTargets;
                                BattleCharacterBase[] friendEffectedCharacters, enemyEffectedCharacters;

                                var skill = catalog.getItemFromGuid(selectedItem.item.expendableWithSkill.skill) as Common.Rom.NSkill;

                                if (skill != null)
                                {
                                    GetSkillTarget(skill, out friendEffectTargets, out enemyEffectTargets);

                                    EffectSkill(activeCharacter, skill, friendEffectTargets.ToArray(), enemyEffectTargets.ToArray(), damageTextList, recoveryStatusInfo,
                                        out friendEffectedCharacters, out enemyEffectedCharacters, out reflections, true);

                                    activeCharacter.targetCharacter = friendEffectedCharacters.Union(enemyEffectedCharacters).Union(reflections.Select(x => x.target)).ToArray();

                                    BattleEventControllerBase.lastSkillTargetType = (int)(skill?.option.target ?? Rom.TargetType.NONE);
                                    battleEvents.setLastSkillTargetIndex(friendEffectTargets, enemyEffectTargets);

                                    if (activeCharacter.targetCharacter.Count() > 0)
                                    {
                                        // アイテムの数を減らす
                                        // reduce the number of items
                                        if (selectedItem.item.Consumption)
                                            selectedItem.num--;
                                        party.SetItemNum(selectedItem.item.guId, selectedItem.num);

                                        activeCharacter.commandFriendEffectCharacters.AddRange(friendEffectedCharacters);
                                        activeCharacter.commandEnemyEffectCharacters.AddRange(enemyEffectedCharacters);

                                        activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.HIT;
                                    }
                                }
                            }
                        }
                        else if (selectedItem.item.IsExpandable)
                        {
                            if (activeCharacter.targetCharacter != null)
                            {
                                var useRest = activeCharacter.targetCharacter.Length;

                                var useItem = false;

                                foreach (var target in activeCharacter.targetCharacter)
                                {
                                    if (IsHitRange(activeCharacter, selectedItem.item, target) && UseItem(selectedItem.item, target, damageTextList, recoveryStatusInfo))
                                    {
                                        activeCharacter.lastHitCheckResult = BattleCharacterBase.HitCheckResult.HIT;
                                        useItem = true;
                                        useRest--;

                                        // アイテムの数を減らす
                                        // reduce the number of items
                                        if (selectedItem.item.Consumption)
                                            selectedItem.num--;
                                        party.SetItemNum(selectedItem.item.guId, selectedItem.num);
                                    }

                                    target.CommandReactionType = ReactionType.Heal;


                                    if (useRest <= 0)
                                    {
                                        break;
                                    }
                                }

                                if (!useItem)
                                {
                                    // アイテムの効果が無かったので使わなかった事にする
                                    // Since the item had no effect, I decided not to use it.
                                    activeCharacter.selectedItem = null;
                                }
                            }
                        }
                    }
                    break;

                case BattleCommandType.Position:
                    if (activeCharacter.targetCharacter != null)
                    {
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Pokemon", 
                            string.Format("Executing Position command for {0}", activeCharacter.Name));
                        
                        // Pokemon-style switching: Handle both player and enemy switching
                        if (activeCharacter is BattlePlayerData)
                        {
                            // Player switching
                            var incomingCharacter = activeCharacter.targetCharacter[0] as BattlePlayerData;
                            var outgoingCharacter = activeCharacter as BattlePlayerData;
                            
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Pokemon", 
                                string.Format("Switching {0} out for {1}", outgoingCharacter.Name, incomingCharacter.Name));
                            
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Pokemon", 
                                string.Format("Direction fix: {0} had direction {1}, {2} will face {3} (toward enemies)", 
                                outgoingCharacter.Name, outgoingCharacter.directionRad, incomingCharacter.Name, incomingCharacter.directionRad));
                            
                            // Find the position of the outgoing character
                            var activeIdx = playerData.IndexOf(outgoingCharacter);
                            
                            if (activeIdx < 0)
                            {
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Pokemon", 
                                    string.Format("ERROR: Could not find {0} in playerData list. Switching cancelled.", outgoingCharacter.Name));
                                break;
                            }
                            
                            // SWITCH CLEAR SYSTEM: Clear #switchclear status ailments from outgoing player
                            ClearSwitchClearableStatuses(outgoingCharacter);
                            
                            // Set up the incoming character for battle
                            incomingCharacter.IsBattle = true;
                            incomingCharacter.IsStock = false;
                            incomingCharacter.SetPosition(outgoingCharacter.pos);
                            
                            // Fix direction: Players should always face toward enemies (north/up)
                            incomingCharacter.directionRad = -(float)Math.PI ; // Face UP/north (toward enemies)
                            
                            // Set up the outgoing character as reserve
                            outgoingCharacter.IsBattle = false;
                            outgoingCharacter.IsStock = true;
                            
                            // Update all relevant lists (with bounds checking)
                            if (activeIdx >= 0 && activeIdx < playerData.Count)
                            {
                            playerData[activeIdx] = incomingCharacter;
                            targetPlayerData[activeIdx] = incomingCharacter;
                            playerViewData[activeIdx] = incomingCharacter;
                            }
                            else
                            {
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Pokemon", 
                                    string.Format("ERROR: activeIdx {0} is out of range for playerData (count: {1})", activeIdx, playerData.Count));
                                break;
                            }
                            
                            // Move outgoing character to stock and remove incoming from stock
                            stockPlayerData.Add(outgoingCharacter);
                            stockPlayerData.Remove(incomingCharacter);
                            
                            // RETARGETING SYSTEM: Update all pending attacks to target the incoming character
                            RetargetAllAttacksFromTo(outgoingCharacter, incomingCharacter);
                            
                            // Update 3D viewer if available - regenerate the actor for proper visual switching
                            if (battleViewer is BattleViewer3D viewer3D)
                            {
                                var oldActor = viewer3D.searchFromActors(outgoingCharacter);
                                if (oldActor != null)
                                {
                                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Pokemon", 
                                        "Regenerating 3D actor for visual switching");
                                    
                                    // Store the position and other important data
                                    var position = oldActor.mapChr.getPosition();
                                    var direction = incomingCharacter.directionRad; // Use the corrected direction
                                    var playerIndex = activeIdx;
                                    
                                    // Release the old actor
                                    oldActor.Release();
                                    
                                    // Create new actor for the incoming character
                                    BattleActor.party = owner.data.party;
                                    var newActor = BattleActor.GenerateFriend(catalog, incomingCharacter.player, playerIndex, playerData.Count);
                                    newActor.source = incomingCharacter;
                                    
                                    // Set position and direction
                                    newActor.mapChr.setPosition(position);
                                    newActor.mapChr.setDirectionFromRadian(direction);
                                    
                                    // Update references
                                    incomingCharacter.mapChr = newActor.mapChr;
                                    incomingCharacter.actionHandler = outgoingCharacter.actionHandler;
                                    
                                    // Replace in the friends list
                                    viewer3D.friends[playerIndex] = newActor;
                                    
                                    // Trigger entrance animation
                                    newActor.queueActorState(BattleActor.ActorStateType.APPEAR, "walk", 20);
                                    newActor.queueActorState(BattleActor.ActorStateType.APPEAR_END);
                                }
                                else
                                {
                                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Pokemon", 
                                        "ERROR: Could not find 3D actor for switching");
                                }
                            }
                            else
                            {
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Pokemon", 
                                    "No 3D viewer available for visual switching");
                            }
                            
                            // Update the active character reference to the new character
                            activeCharacter = incomingCharacter;

                        MovePlayerPosition();
                            
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Pokemon", 
                                string.Format("Switch complete! {0} is now active", incomingCharacter.Name));
                        }
                        else if (activeCharacter is BattleEnemyData)
                        {
                            // Enemy switching
                            var incomingEnemy = activeCharacter.targetCharacter[0] as BattleEnemyData;
                            var outgoingEnemy = activeCharacter as BattleEnemyData;
                            
                            // Find the position of the outgoing enemy
                            var activeIdx = enemyData.IndexOf(outgoingEnemy);
                            
                            if (activeIdx < 0)
                            {
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Pokemon", 
                                    string.Format("ERROR: Could not find {0} in enemyData list. Switching cancelled.", outgoingEnemy.Name));
                                break;
                            }
                            
                            // SWITCH CLEAR SYSTEM: Clear #switchclear status ailments from outgoing enemy
                            ClearSwitchClearableStatuses(outgoingEnemy);
                            
                            // Set up the incoming enemy for battle
                            incomingEnemy.IsBattle = true;
                            incomingEnemy.IsStock = false;
                            incomingEnemy.SetPosition(outgoingEnemy.pos);
                            incomingEnemy.directionRad = outgoingEnemy.directionRad;
                            
                            // Set up the outgoing enemy as reserve
                            outgoingEnemy.IsBattle = false;
                            outgoingEnemy.IsStock = true;
                            
                            // Update all relevant lists (with bounds checking)
                            if (activeIdx >= 0 && activeIdx < enemyData.Count)
                            {
                            enemyData[activeIdx] = incomingEnemy;
                            targetEnemyData[activeIdx] = incomingEnemy;
                            enemyMonsterViewData[activeIdx] = incomingEnemy;
                            }
                            else
                            {
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Pokemon", 
                                    string.Format("ERROR: activeIdx {0} is out of range for enemyData (count: {1})", activeIdx, enemyData.Count));
                                break;
                            }
                            
                            // Move outgoing enemy to stock and remove incoming from stock
                            stockEnemyData.Add(outgoingEnemy);
                            stockEnemyData.Remove(incomingEnemy);
                            
                            // RETARGETING SYSTEM: Update all pending attacks to target the incoming enemy
                            RetargetAllAttacksFromTo(outgoingEnemy, incomingEnemy);
                            
                            // Update 3D viewer if available
                            if (battleViewer is BattleViewer3D viewer3D)
                            {
                                // Handle the visual switching for enemies - update existing actor to point to new character
                                var actor = viewer3D.searchFromActors(outgoingEnemy);
                                if (actor != null)
                                {
                                    actor.source = incomingEnemy;
                                    incomingEnemy.mapChr = actor.mapChr;
                                    incomingEnemy.actionHandler = outgoingEnemy.actionHandler;
                                }
                            }
                        }
                    }
                    break;

                case BattleCommandType.Nothing:
                case BattleCommandType.Nothing_Down:
                case BattleCommandType.Cancel:
                case BattleCommandType.Skip:
                case BattleCommandType.PlayerEscape:
                case BattleCommandType.MonsterEscape:
                    activeCharacter.targetCharacter = null;
                    break;
            }

            battleViewer.SetDamageTextInfo(damageTextList);

            if (activeCharacter.targetCharacter != null)
            {
                foreach (var target in activeCharacter.targetCharacter)
                {
                    target.ConsistancyConsumptionStatus(gameSettings);

                    if (target.HitPoint < 0) target.HitPoint = 0;
                    if (target.HitPoint > target.MaxHitPoint) target.HitPoint = target.MaxHitPoint;

                    if (target.MagicPoint < 0) target.MagicPoint = 0;
                    if (target.MagicPoint > target.MaxMagicPoint) target.MagicPoint = target.MaxMagicPoint;
                }
            }

            switch (activeCharacter.selectedBattleCommandType)
            {
                case BattleCommandType.PlayerEscape:
                    ChangeBattleState(BattleState.PlayerChallengeEscape);
                    break;

                case BattleCommandType.MonsterEscape:
                    Audio.PlaySound(owner.se.escape);
                    ChangeBattleState(BattleState.MonsterEscape);
                    break;
                case BattleCommandType.Item:
                    ChangeBattleState(BattleState.SetCommandEffect);
                    break;
                default:
                    ChangeBattleState(BattleState.SetCommandEffect);
                    break;
            }
        }

        static T RandomElementAt<T>(IEnumerable<T> ie)
        {
            var cnt = ie.Count();
            if (cnt == 0)
                return default;

            return ie.ElementAt(GameMain.instance.mapScene.GetRandom(cnt));
        }

        private void UpdateBattleState_ExecuteReflection()
        {
            var gs = catalog.getGameSettings();
            var damageTextList = new List<BattleDamageTextInfo>();

            // 反射対象に効果を適用する
            // Apply effects to reflective objects
            var targets = new List<BattleCharacterBase>();
            foreach(var entry in reflections)
            {
                var src = entry.target;
                var dest = activeCharacter;
                if (entry.reflect.ReflectionTarget == Rom.BattleImpactReflectionPercentParam.ReflectionTargetType.Random)
                {
                    if (entry.isFriendEffect)
                        dest = RandomElementAt(activeCharacter.EnemyPartyRefMember.Where(x => x.HitPoint > 0));
                    else
                        dest = RandomElementAt(activeCharacter.FriendPartyRefMember.Where(x => x.HitPoint > 0));
                }
                if (dest == null)
                    break;

                BattleCharacterBase[] friendEffectTargets, enemyEffectTargets;
                if(entry.isFriendEffect)
                {
                    friendEffectTargets = new BattleCharacterBase[] { dest };
                    enemyEffectTargets = new BattleCharacterBase[0];
                }
                else
                {
                    friendEffectTargets = new BattleCharacterBase[0];
                    enemyEffectTargets = new BattleCharacterBase[] { dest };
                }

                BattleCharacterBase[] friendEffectedCharacters, enemyEffectedCharacters;

                EffectSkill(activeCharacter, activeCharacter.selectedSkill, friendEffectTargets.ToArray(), enemyEffectTargets.ToArray(), damageTextList, recoveryStatusInfo,
                    out friendEffectedCharacters, out enemyEffectedCharacters, out _, false);

                if (friendEffectedCharacters.Length + enemyEffectedCharacters.Length > 0)
                    entry.index = targets.Count;

                targets.AddRange(friendEffectedCharacters);
                targets.AddRange(enemyEffectedCharacters);
            }

            activeCharacter.targetCharacter = targets.ToArray();
            battleViewer.SetDamageTextInfo(damageTextList);

            if (activeCharacter.targetCharacter != null)
            {
                foreach (var target in activeCharacter.targetCharacter)
                {
                    target.ConsistancyConsumptionStatus(gameSettings);

                    if (target.HitPoint < 0) target.HitPoint = 0;
                    if (target.HitPoint > target.MaxHitPoint) target.HitPoint = target.MaxHitPoint;

                    if (target.MagicPoint < 0) target.MagicPoint = 0;
                    if (target.MagicPoint > target.MaxMagicPoint) target.MagicPoint = target.MaxMagicPoint;
                }
            }

            ChangeBattleState(BattleState.SetReflectionEffect);
        }

        private void GetSkillTarget(Rom.NSkill skill, out BattleCharacterBase[] friendEffectTargets, out BattleCharacterBase[] enemyEffectTargets)
        {
            activeCharacter.GetSkillTarget(skill, out friendEffectTargets, out enemyEffectTargets,
                activeCharacter.FriendPartyRefMember.Where(x => IsHitRange(activeCharacter, skill.Range, x)),
                activeCharacter.EnemyPartyRefMember.Where(x => IsHitRange(activeCharacter, skill.Range, x)));
        }

        private void SetCounterAction(BattleCharacterBase target, BattleCharacterBase source)
        {
            var enm = target as BattleEnemyData;
            if (enm == null)
                return;

            var actions = GetActiveActions(enm, true, enm.currentActionTurn);
            if (actions.FirstOrDefault() != null)
            {
                target.selectedBattleCommandType = BattleCommandType.Undecided;
                SelectEnemyCommand(enm, false, true, enm.currentActionTurn, actions);
                enm.counterAction = BattleEnemyData.CounterState.COUNTER;
                // 味方を対象にするスキルなら自分をターゲットにしておく
                // If the skill targets an ally, target yourself
                if (target.selectedBattleCommandType == BattleCommandType.Skill &&
                    (target.selectedSkill.option.target == Rom.TargetType.PARTY_ONE || target.selectedSkill.option.target == Rom.TargetType.PARTY_ALL))
                    target.targetCharacter[0] = target;
                // その他は攻撃してきた相手を狙う
                // Others aim at the opponent who attacked
                else if (target.targetCharacter == null)
                    target.targetCharacter = new BattleCharacterBase[] { source };
                else if (target.targetCharacter.Length == 1)
                    target.targetCharacter[0] = source;

                if (target is ExBattleEnemyData)
                    ((ExBattleEnemyData)target).turnGauge = 1;
                //battleEntryCharacters.Insert(commandExecuteMemberCount + 1, target);
            }
            else
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, enm.Name, "No Counter Action.");
            }
        }

        public void InsertAction(BattleActionEntry entry, bool remove = false, bool increment = true)
        {
            if (remove)
            {
                foreach (var forRemove in battleEntryCharacters.Where(x => x.character == entry.character && x.setter == null))
                {
                    forRemove.setter = () => activeCharacter.selectedBattleCommandType = BattleCommandType.Skip;
                }
            }
            battleEntryCharacters.Insert(Math.Min(battleEntryCharacters.Count, commandExecuteMemberCount + (increment ? 1 : 0)), entry);
        }

        // 攻撃を受けた時の状態異常 回復判定
        // Status abnormality recovery judgment when attacked
        private void CheckDamageRecovery(BattleCharacterBase target, int damage)
        {
            var recoveryList = new List<Hero.ConditionInfo>(target.conditionInfoDic.Count);

            foreach (var e in target.conditionInfoDic)
            {
                var info = e.Value;

                if ((info.recovery & Hero.ConditionInfo.RecoveryType.Damage) != 0)
                {
                    info.damageValue -= damage;

                    if (info.damageValue <= 0)
                    {
                        recoveryList.Add(info);

                        break;
                    }
                }
            }

            foreach (var info in recoveryList)
            {
                if (target != activeCharacter)
                {
                    target.selectedBattleCommandType = BattleCommandType.Undecided;
                }

                target.RecoveryCondition(info.condition, battleEvents, Rom.Condition.RecoveryType.Terms);

                if ((target.HitPoint > 0) && (info.rom != null))
                {
                    recoveryStatusInfo.Add(new RecoveryStatusInfo(target, info.rom));
                }
            }
        }

        private void UpdateBattleState_SetStatusMessageText()
        {
            // バトルイベント完了を待つ
            // Wait for battle event to complete
            if (battleEvents.isBusy())
                return;
            battleEvents.clearCurrentProcessingTrigger();

            string message = "";
            bool isDisplayMessage = false;
            var isActionDisabled = false;

            foreach (var e in activeCharacter.conditionInfoDic)
            {
                var condition = e.Value.rom;

                if (!displayedContinueConditions.Contains(e.Key))
                {
                    displayedContinueConditions.Add(e.Key);

                    if ((condition != null) && !condition.IsBattleSlipDamage)
                    {
                        message = string.Format(condition.GetMessage(Rom.MessageParam.ConditionForContinueId), activeCharacter.Name);

                        if (!string.IsNullOrEmpty(message))
                        {
                            isDisplayMessage = true;
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Message", message);
                            break;
                        }
                    }
                }

                if (!isActionDisabled || (condition != null))
                {
                    isActionDisabled = isActionDisabled || condition.IsActionDisabled;
                }
            }

            if (isDisplayMessage)
            {
                battleViewer.SetDisplayMessage(message);

                ChangeBattleState(BattleState.DisplayStatusMessage);
            }
            else
            {
                // コマンドに応じたアクションをアクターにとらせる
                // Make actors take actions according to commands
                activeCharacter.ExecuteCommandStart();

                if (isActionDisabled)
                {
                    ChangeBattleState(BattleState.ExecuteBattleCommand);
                }
                else
                {
                    ChangeBattleState(BattleState.SetCommandMessageText);
                }
            }
        }

        private void UpdateBattleState_DisplayStatusMessage()
        {
            if (battleStateFrameCount > 30)
            {
                battleViewer.CloseWindow();

                ChangeBattleState(BattleState.SetStatusMessageText);
            }
        }

        private void UpdateBattleState_DisplayStatusDamage()
        {
            bool isEndPlayerStatusUpdate = playerData.Select(player => UpdateBattleStatusData(player)).All(isUpdated => isUpdated == false);
            isEndPlayerStatusUpdate |= enemyData.Select(enemy => UpdateBattleStatusData(enemy)).All(isUpdated => isUpdated == false);

            if (!isEndPlayerStatusUpdate) statusUpdateTweener.Update();

            if (!battleViewer.IsPlayDamageTextAnimation && isEndPlayerStatusUpdate)
            {
                battleViewer.CloseWindow();

                ChangeBattleState(BattleState.CheckBattleCharacterDown2);
            }
        }

        private void UpdateBattleState_SetCommandMessageText()
        {
            string message = "";

            switch (activeCharacter.selectedBattleCommandType)
            {
                case BattleCommandType.Nothing:
                    message = string.Format(gameSettings.glossary.battle_wait, activeCharacter.Name);
                    break;
                case BattleCommandType.Attack:
                case BattleCommandType.Miss:
                    message = string.Format(gameSettings.glossary.battle_attack, activeCharacter.Name);
                    break;
                case BattleCommandType.Critical:
                case BattleCommandType.ForceCritical:
                    message = string.Format(gameSettings.glossary.battle_critical, activeCharacter.Name);
                    break;
                case BattleCommandType.Guard:
                    message = string.Format(gameSettings.glossary.battle_guard, activeCharacter.Name);
                    break;
                case BattleCommandType.Charge:
                    message = string.Format(gameSettings.glossary.battle_charge, activeCharacter.Name);
                    break;
                case BattleCommandType.SameSkillEffect:
                    message = activeCharacter.selectedBattleCommand.name;
                    break;
                case BattleCommandType.Skill:
                    message = string.Format(gameSettings.glossary.battle_skill, activeCharacter.Name, activeCharacter.selectedSkill.name);
                    break;
                case BattleCommandType.Item:
                    message = string.Format(gameSettings.glossary.battle_item, activeCharacter.Name, activeCharacter.selectedItem.item.name);
                    break;
                case BattleCommandType.PlayerEscape:
                    message = gameSettings.glossary.battle_escape_command;
                    break;
                case BattleCommandType.MonsterEscape:
                    message = string.Format(gameSettings.glossary.battle_enemy_escape, activeCharacter.Name);
                    break;
            }

            battleViewer.SetDisplayMessage(message);
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Message", message);

            ChangeBattleState(BattleState.DisplayMessageText);
        }

        private void UpdateBattleState_DisplayMessageText()
        {
            if ((battleStateFrameCount > 20 || battleViewer.HasNoMessageWindow() || Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU)) && isReady3DCamera() && isReadyActor())
            {
                ChangeBattleState(BattleState.ExecuteBattleCommand);
            }
        }

        private bool IsBusyWaitForCommon(Guid inEventId)
        {
            return ((inEventId != Guid.Empty) && battleEvents.isBusy(inEventId));
        }

        private void UpdateBattleState_SetCommandEffect()
        {
            for (int i = 0; i < waitForCommons.Length; i++)
            {
                if (IsBusyWaitForCommon(waitForCommons[i]))
                {
                    return;
                }

                waitForCommons[i] = Guid.Empty;
            }

            if (IsBusyWaitForCommon(waitForCommon))
            {
                return;
            }

            waitForCommon = Guid.Empty;

            ClearCommandEffect();
            GetCommandEffectImpl(activeCharacter, catalog, out var friendEffectGuid, out var enemyEffectGuid);

            // EVOLUTION SYSTEM TRIGGER
            var isDigiEvo = GameMain.instance.data.system.GetSwitch("executeDigievo", Guid.Empty, false);
            
            // Continue evolution process if already started
            if (showingShoices || selectingEvoDone)
            {
                Tools.PushLog("Continuing evolution process...");
                ChoiceEvolution();
                return; // Don't proceed with normal skill effects while evolving
            }
            
            if (activeCharacter.selectedBattleCommandType == BattleCommandType.SameSkillEffect)
            {
                if (isDigiEvo) // Evolution skill triggered
                {
                    if (activeCharacter.mapChr.isChangeMotionAvailable() && activeCharacter.mapChr.currentMotion != "wait" && battleEvents.GetChoicesResult() == -1) 
                        activeCharacter.mapChr.playMotion("wait", 0.2f, false, false);
                    Tools.PushLog("Starting digievolution");
                    if (!PrepareEvolution()) return;
                    ChoiceEvolution();
                    return; // Don't proceed with normal skill effects when starting evolution
                }
            }

            if (activeCharacter.targetCharacter != null)
            {
                // 敵がスキルを使用した場合、敵味方を反転させる
                // Inverts allies and enemies when using a skill
                bool isInvert = !activeCharacter.IsHero;

                if (isInvert)
                {
                    var tmp = friendEffectGuid;
                    friendEffectGuid = enemyEffectGuid;
                    enemyEffectGuid = tmp;
                }

                foreach (var target in activeCharacter.targetCharacter)
                {
                    if (friendEffectGuid != Guid.Empty && target.IsHero)
                    {
                        battleViewer.SetBattlePlayerEffect(friendEffectGuid, target);
                    }
                    else if (enemyEffectGuid != Guid.Empty && !target.IsHero)
                    {
                        battleViewer.SetBattleMonsterEffect(enemyEffectGuid, target);
                    }
                }
            }

            ChangeBattleState(BattleState.DisplayCommandEffect);
            EffectDrawer3D.ResetStartWait();
        }

        private void UpdateBattleState_SetReflectionEffect()
        {
			for (int i = 0; i < waitForCommons.Length; i++)
			{
				if (IsBusyWaitForCommon(waitForCommons[i]))
				{
                    return;
                }

                waitForCommons[i] = Guid.Empty;
            }

            if (IsBusyWaitForCommon(waitForCommon))
            {
                return;
            }

            waitForCommon = Guid.Empty;

            ClearCommandEffect();
            GetCommandEffectImpl(activeCharacter, catalog, out var friendEffectGuid, out var enemyEffectGuid);

            if (activeCharacter.targetCharacter != null && catalog.getGameSettings().ShowReflectionDetail)
            {
                foreach (var entry in reflections)
                {
                    if (entry.index < 0)
                        continue;

                    var target = activeCharacter.targetCharacter[entry.index];

                    if (friendEffectGuid != Guid.Empty && entry.isFriendEffect)
                    {
                        battleViewer.SetBattlePlayerEffect(friendEffectGuid, target);
                    }
                    else if (enemyEffectGuid != Guid.Empty && !entry.isFriendEffect)
                    {
                        battleViewer.SetBattleMonsterEffect(enemyEffectGuid, target);
                    }

                    if (!battleViewer.reflectionSourceList.ContainsKey(target))
                        battleViewer.reflectionSourceList.Add(target, entry.target);
                }
            }

            reflections = null;
            ChangeBattleState(BattleState.DisplayCommandEffect);
            EffectDrawer3D.ResetStartWait();
        }

        private void ClearCommandEffect()
        {
            battleViewer.effectDrawTargetPlayerList.Clear();
            battleViewer.effectDrawTargetMonsterList.Clear();
            battleViewer.defeatEffectDrawTargetList.Clear();
            battleViewer.reflectionSourceList.Clear();
        }

        private static void GetCommandEffectImpl(BattleCharacterBase activeCharacter, Catalog catalog, out Guid friendEffectGuid, out Guid enemyEffectGuid)
        {
            friendEffectGuid = Guid.Empty;
            enemyEffectGuid = Guid.Empty;

            switch (activeCharacter.selectedBattleCommandType)
            {
                case BattleCommandType.Attack:
                case BattleCommandType.Critical:
                case BattleCommandType.ForceCritical:
                case BattleCommandType.Miss:
                    EffectDrawer3D.setStartWait(0.1f);
                    if (activeCharacter.lastHitCheckResult == BattleCharacterBase.HitCheckResult.HIT)
                    {
                        friendEffectGuid = enemyEffectGuid = activeCharacter.AttackEffect;
                    }
                    else if (activeCharacter.lastHitCheckResult == BattleCharacterBase.HitCheckResult.CRITICAL)
                    {
                        friendEffectGuid = enemyEffectGuid =
                            (activeCharacter.CriticalEffect == Guid.Empty) ? activeCharacter.AttackEffect : activeCharacter.CriticalEffect;
                    }
                    else
                    {
                        friendEffectGuid = activeCharacter.CriticalEffect;
                        enemyEffectGuid = activeCharacter.AttackEffect;
                    }
                    break;

                case BattleCommandType.Guard:
                    break;

                case BattleCommandType.Charge:
                    break;

                case BattleCommandType.SameSkillEffect:
                    if (activeCharacter.targetCharacter != null)
                    {
                        var skill = catalog.getItemFromGuid(activeCharacter.selectedBattleCommand.refGuid) as Rom.NSkill;

                        switch (skill.option.target)
                        {
                            case Rom.TargetType.PARTY_ONE:
                            case Rom.TargetType.PARTY_ALL:
                            case Rom.TargetType.SELF:
                            case Rom.TargetType.OTHERS:
                                friendEffectGuid = skill.friendEffect.effect;
                                break;

                            case Rom.TargetType.ENEMY_ONE:
                            case Rom.TargetType.ENEMY_ALL:
                                enemyEffectGuid = skill.enemyEffect.effect;
                                break;

                            case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                            case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                            case Rom.TargetType.SELF_ENEMY_ONE:
                            case Rom.TargetType.SELF_ENEMY_ALL:
                            case Rom.TargetType.ALL:
                            case Rom.TargetType.OTHERS_ENEMY_ONE:
                            case Rom.TargetType.OTHERS_ALL:
                                friendEffectGuid = skill.friendEffect.effect;
                                enemyEffectGuid = skill.enemyEffect.effect;
                                break;
                        }
                    }
                    break;

                case BattleCommandType.Skill:
                    if (activeCharacter.targetCharacter != null)
                    {
                        switch (activeCharacter.selectedSkill.option.target)
                        {
                            case Rom.TargetType.PARTY_ONE:
                            case Rom.TargetType.PARTY_ALL:
                            case Rom.TargetType.SELF:
                            case Rom.TargetType.OTHERS:
                                friendEffectGuid = activeCharacter.selectedSkill.friendEffect.effect;
                                break;

                            case Rom.TargetType.ENEMY_ONE:
                            case Rom.TargetType.ENEMY_ALL:
                                enemyEffectGuid = activeCharacter.selectedSkill.enemyEffect.effect;
                                break;

                            case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                            case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                            case Rom.TargetType.SELF_ENEMY_ONE:
                            case Rom.TargetType.SELF_ENEMY_ALL:
                            case Rom.TargetType.ALL:
                            case Rom.TargetType.OTHERS_ENEMY_ONE:
                            case Rom.TargetType.OTHERS_ALL:
                                friendEffectGuid = activeCharacter.selectedSkill.friendEffect.effect;
                                enemyEffectGuid = activeCharacter.selectedSkill.enemyEffect.effect;
                                break;
                        }
                    }
                    break;

                case BattleCommandType.Item:
					if (activeCharacter.selectedItem == null)
					{
                        break;
					}

                    if (activeCharacter.selectedItem.item.IsExpandableWithSkill)
                    {
                        var skill = (Common.Rom.NSkill)catalog.getItemFromGuid(activeCharacter.selectedItem.item.expendableWithSkill.skill);
                        if (skill != null)
                        {
                            switch (skill.option.target)
                            {
                                case Rom.TargetType.PARTY_ONE:
                                case Rom.TargetType.PARTY_ALL:
                                case Rom.TargetType.SELF:
                                case Rom.TargetType.OTHERS:
                                    friendEffectGuid = skill.friendEffect.effect;
                                    break;

                                case Rom.TargetType.ENEMY_ONE:
                                case Rom.TargetType.ENEMY_ALL:
                                    enemyEffectGuid = skill.enemyEffect.effect;
                                    break;

                                case Rom.TargetType.ALL:                   // 敵味方全員 / All enemies and allies
                                case Rom.TargetType.SELF_ENEMY_ONE:        // 自分と敵一人 / me and one enemy
                                case Rom.TargetType.SELF_ENEMY_ALL:        // 自分と敵全員 / myself and all my enemies
                                case Rom.TargetType.OTHERS_ENEMY_ONE:      // 自分以外と敵一人 / Other than yourself and one enemy
                                case Rom.TargetType.OTHERS_ALL:            // 自分以外の敵味方全員 / All enemies and allies other than yourself
                                case Rom.TargetType.PARTY_ONE_ENEMY_ALL:   // 味方一人と敵全員 / One ally and all enemies
                                case Rom.TargetType.PARTY_ALL_ENEMY_ONE:   // 味方全員と敵一人 / All allies and one enemy
                                    friendEffectGuid = skill.friendEffect.effect;
                                    enemyEffectGuid = skill.enemyEffect.effect;
                                    break;
                            }
                        }
                    }
                    else if (activeCharacter.selectedItem.item.IsExpandable)
                    {
                        {
                            friendEffectGuid = activeCharacter.selectedItem.item.expendable.effect;
                        }
                    }
                    break;
            }

            if(enemyEffectGuid == GUID_USE_ATTACKEFFEECT)
            {
                enemyEffectGuid = activeCharacter.AttackEffect;
            }
        }

        private void UpdateBattleState_DisplayCommandEffect()
        {
            if (battleViewer.IsEffectAllowShowDamage)
            {
                battleViewer.SetupDamageTextAnimation();

                if (activeCharacter.selectedBattleCommandType == BattleCommandType.Skill && activeCharacter.targetCharacter == null)
                {
                    battleViewer.SetDisplayMessage(string.Format(gameSettings.glossary.battle_skill_failed, activeCharacter.skillFailCauses));
                }

                if (activeCharacter.targetCharacter != null)
                {
                    foreach (var target in activeCharacter.targetCharacter)
                    {
                        target.CommandReactionStart();
                    }
                }

                foreach (var player in playerData)
                {
                    if (owner.debugSettings.battleHpAndMpMax)
                    {
                        foreach (var info in gameSettings.CastStatusParamInfoList)
                        {
                            if (info.Consumption)
                            {
                                player.consumptionStatusValue.SetStatus(info.guId, player.baseStatusValue.GetStatus(info.guId));
                            }
                        }

                        player.HitPoint = player.MaxHitPoint;
                        player.MagicPoint = player.MaxMagicPoint;
                    }

                    SetNextBattleStatus(player);
                }

                foreach (var enemy in enemyData)
                {
                    SetNextBattleStatus(enemy);
                }

                statusUpdateTweener.Begin(0, 1.0f, 30);

                ChangeBattleState(BattleState.DisplayDamageText);
            }
        }

        /// <summary>
        /// 連続攻撃の種別
        /// Continuous attack type
        /// </summary>
        enum ContinuousType
        {
            NONE,
            ATTACK,
            CONTINUOUS_ACTION,
        }
        private ContinuousType CheckAttackContinue()
        {
            // 状態異常で攻撃回数が追加されているか？
            // Is the number of attacks added due to status ailments?
            switch (activeCharacter.selectedBattleCommandType)
            {
                case BattleCommandType.Attack:
                case BattleCommandType.Critical:
                case BattleCommandType.ForceCritical:
                case BattleCommandType.Miss:
                    if (attackCount < 1 + activeCharacter.AttackAddCondition)
                    {
                        foreach (var target in activeCharacter.targetCharacter)
                        {
                            if (!target.IsDeadCondition())
                            {
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, activeCharacter.Name,
                                    string.Format("Continuous Attack / Rest : {0}", activeCharacter.AttackAddCondition - attackCount));
                                return ContinuousType.ATTACK;
                            }
                        }
                    }
                    break;
            }

            // 敵専用の従来からある連続行動
            // Traditional continuous action for enemies only
            if (activeCharacter is BattleEnemyData)
            {
                if (((BattleEnemyData)activeCharacter).continuousAction)
                {
                    activeCharacter.ExecuteCommandEnd();
                    activeCharacter.selectedBattleCommandType = BattleCommandType.Undecided;
                    SelectEnemyCommand((BattleEnemyData)activeCharacter, true, false);

                    // 連続行動中の「何もしない」はメッセージを出さない
                    // \
                    // TODO : できれば中間に何もしないがある場合もスキップにしたい
                    // TODO : If possible, I want to skip even if there is nothing in between
                    if (activeCharacter.selectedBattleCommandType != BattleCommandType.Nothing)
                    {
                        return ContinuousType.CONTINUOUS_ACTION;
                    }
                }

                ((BattleEnemyData)activeCharacter).alreadyExecuteActions.Clear();
            }

            return ContinuousType.NONE;
        }

        private bool IsUpdateBattleStatusDataAll()
        {
			bool isUpdated = false;

			foreach (var player in playerData)
			{
				isUpdated |= UpdateBattleStatusData(player);
			}
			foreach (var enemy in enemyData)
			{
				isUpdated |= UpdateBattleStatusData(enemy);
			}

			if (isUpdated)
			{
				statusUpdateTweener.Update();
			}

            return isUpdated;
		}

		private void UpdateBattleState_DisplayDamageText()
        {
			bool isUpdated = IsUpdateBattleStatusDataAll();
			
            // ダメージ用テキストとステータス用ゲージのアニメーションが終わるまで待つ
            // Wait for damage text and status gauge animation to finish
            if (!battleEvents.isBusy(false) && battleViewer.IsEffectEndPlay && !battleViewer.IsPlayDamageTextAnimation && !isUpdated && isReady3DCamera() &&
                ((activeCharacter.targetCharacter != null || battleStateFrameCount > 60)))
            {
                bool complete = true;

                if (activeCharacter.targetCharacter != null)
                {
                    foreach (var target in activeCharacter.targetCharacter)
                    {
                        if (!battleViewer.IsEndMotion(target))
                        {
                            complete = false;
                            break;
                        }

                        target.CommandReactionEnd();
                    }
                }

                if (complete)
                {
                    // 通常は状態変化付与のメッセージに移行する
                    // Normally, the message changes to a status change message.
                    var nextBattleState = BattleState.SetConditionMessageText;

                    if (reflections != null && reflections.Length > 0)
                    {
                        // 反射が発生している？
                        // Are reflections occurring?
                        nextBattleState = BattleState.ExecuteReflection;
                    }
                    else
                    {
                        // 連続行動がある？
                        // Is there a continuous action?
                        var continuous = CheckAttackContinue();

                        battleViewer.CloseWindow();

                        if (continuous == ContinuousType.NONE)
                        {
                            battleEvents.start(Rom.Script.Trigger.BATTLE_AFTER_ACTION);
                            activeCharacter.ExecuteCommandEnd();
                            activeCharacter.recentBattleCommandType = activeCharacter.selectedBattleCommandType;

                            // #15019 でコメントアウトしたが、#16138 で最有効化し、かわりに recentBattleCommandType を実装。
                            // Commented out in #15019, but re-enabled in #16138 and implemented recentBattleCommandType instead.
                            // 連続行動時にバトルが進行しないことがあるバグが発生したため
                            // Due to a bug that sometimes caused the battle to not progress during continuous actions.
                            activeCharacter.selectedBattleCommandType = BattleCommandType.Nothing;
                        }
                        else if (continuous == ContinuousType.CONTINUOUS_ACTION)
                        {
                            battleEvents.start(Rom.Script.Trigger.BATTLE_AFTER_ACTION);
                            nextBattleState = BattleState.ReadyExecuteCommand;
                        }
                        else if (continuous == ContinuousType.ATTACK)
                        {
                            nextBattleState = BattleState.ExecuteBattleCommand;
                        }
                    }

                    ChangeBattleState(nextBattleState);
                }
            }
        }

        private void UpdateBattleState_SetConditionMessageText()
        {
            if (battleEvents.isBusy())
                return;
            battleEvents.clearCurrentProcessingTrigger();
            if (activeCharacter != null)
                activeCharacter.recentBattleCommandType = null;

            string message = "";
            bool isDisplayMessage = false;

            foreach (var e in displayedSetConditionsDic)
            {
                retry:

                if (e.Value.Count == 0)
                {
                    continue;
                }

                var condition = e.Value[0];

                message = string.Format(e.Key.IsHero ? condition.GetMessage(Rom.MessageParam.ConditionForAllyId) : condition.GetMessage(Rom.MessageParam.ConditionForEnemyId), e.Key.Name);

                e.Value.Remove(condition);

                if (string.IsNullOrEmpty(message))
                {
                    goto retry;
                }
                else
                {
                    isDisplayMessage = true;

                    break;
                }
            }

            if (isDisplayMessage)
            {
                battleViewer.SetDisplayMessage(message);

                ChangeBattleState(BattleState.DisplayConditionMessageText);
            }
            else
            {
                ChangeBattleState(BattleState.CheckCommandRecoveryStatus);
            }
        }

        private void UpdateBattleState_DisplayConditionMessageText()
        {
            if ((battleStateFrameCount > 30 || Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU)) && isReady3DCamera() && isReadyActor())
            {
                ChangeBattleState(BattleState.SetConditionMessageText);
            }
        }

        private void UpdateBattleState_CheckCommandRecoveryStatus()
        {
            // バトルイベントが処理中だったら、スキル・アイテム名のウィンドウだけ閉じてまだ待機する
            // If the battle event is being processed, just close the skill/item name window and wait.
            if (battleEvents.isBusy())
                return;
            battleEvents.clearCurrentProcessingTrigger();

            // 状態異常が継続している場合は表示しない（ダメージ付きの睡眠スキルなどで発生する可能性あり）
            // Does not display if abnormal status continues (may occur with sleep skills with damage)
            while (recoveryStatusInfo.Count > 0 && recoveryStatusInfo[0].IsContinued)
                recoveryStatusInfo.RemoveAt(0);

            if (recoveryStatusInfo.Count == 0)
            {
                ChangeBattleState(BattleState.CheckBattleCharacterDown1);
            }
            else
            {
                ((BattleViewer3D)battleViewer).RecoveryConditionMotion(recoveryStatusInfo[0]);
                battleViewer.SetDisplayMessage(recoveryStatusInfo[0].GetMessage(gameSettings));

                ChangeBattleState(BattleState.DisplayCommandRecoveryStatus);
            }
        }

        private void UpdateBattleState_DisplayCommandRecoveryStatus()
        {
            if (string.IsNullOrEmpty(battleViewer.displayMessageText) ||
                battleStateFrameCount >= 30 || Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU))
            {
                recoveryStatusInfo.RemoveAt(0);

                ChangeBattleState(BattleState.CheckCommandRecoveryStatus);
            }
        }

        private void UpdateBattleState_CheckBattleCharacterDown1()
        {
            if (battleEvents.isBusy())
                return;
            battleEvents.clearCurrentProcessingTrigger();

            ClearCommandEffect();

            CheckBattleCharacterDown();



            ChangeBattleState(BattleState.FadeMonsterImage1);
        }

		private void UpdateBattleState_FadeMonsterImage1(BattleState nextBattleState)
		{
			//if (battleViewer.IsFadeEnd)
			{
				ChangeBattleState(nextBattleState);
			}
		}

		private void UpdateBattleState_BattleFinishCheck1(BattleState nextBattleState)
        {
            if (battleEvents.isBusy())
                return;
            battleEvents.clearCurrentProcessingTrigger();

            var battleResult = CheckBattleFinish();

            if (battleResult == BattleResultState.NonFinish)
            {
                ChangeBattleState(nextBattleState);
            }
            else if (battleStateFrameCount >= 30)
            {
                battleEvents.setBattleResult(battleResult);
                ChangeBattleState(BattleState.StartBattleFinishEvent);
            }
        }

        private void UpdateBattleState_ProcessPoisonStatus()
        {
            var gs = catalog.getGameSettings();

            if ((playerData.Cast<BattleCharacterBase>().Contains(activeCharacter) || enemyData.Cast<BattleCharacterBase>().Contains(activeCharacter)))
            {
                var isSlipDamage = false;
                var damageTextList = new List<BattleDamageTextInfo>();

                foreach (var e in activeCharacter.conditionInfoDic.Values.ToArray())
                {
                    var condition = e.rom;

					if (condition == null)
					{
                        continue;
					}

                    if (condition.EffectParamSettings.UseOldEffectParam)
                    {
                        if (condition.slipDamage && (condition.battleSlipDamageCycle != 0) && ((e.damageTurnCount % condition.battleSlipDamageCycle) == 0))
                        {
                            var damage = 0;

                            switch (condition.battleSlipDamageType)
                            {
                                case Common.Rom.Condition.SlipDamageType.Direct:
                                    damage = condition.battleSlipDamageParam;
                                    break;
                                case Common.Rom.Condition.SlipDamageType.HPPercent:
                                    damage = activeCharacter.HitPoint * condition.battleSlipDamageParam;
                                    damage = (damage > 100) ? damage / 100 : 1;
                                    break;
                                case Common.Rom.Condition.SlipDamageType.MaxHPPercent:
                                    damage = activeCharacter.MaxHitPoint * condition.battleSlipDamageParam;
                                    damage = (damage > 100) ? damage / 100 : 1;
                                    break;
                                default:
                                    break;
                            }

                            // キャストのパラメータによる軽減
                            // Mitigation by casting parameters
                            damage = (int)Math.Ceiling((double)damage * (100 - activeCharacter.GetStatus(gameSettings, Rom.GameSettings.PoisonDamageReductionPercentStatusKey)) / 100);

                            if (damage > 0)
                            {
                                activeCharacter.HitPoint -= damage;
                                activeCharacter.consumptionStatusValue.SubStatus(gs.maxHPStatusID, damage);

                                if (activeCharacter.HitPoint < 0)
                                {
                                    activeCharacter.HitPoint = 0;
                                }

                                activeCharacter.consumptionStatusValue.Max(gs.maxHPStatusID, 0);

                                // 毒ダメージで睡眠を解除する場合はコメントアウトを外す
                                // Uncomment out if you want to cancel sleep with poison damage
                                //CheckDamageRecovery(activeCharacter, damage);

                                string message = string.Format(condition.GetMessage(Rom.MessageParam.ConditionForContinueId), activeCharacter.Name);

                                battleViewer.SetDisplayMessage(message);

                                // TODO 複数表示対応
                                // TODO Multiple display support
                                var damageText = new List<BattleDamageTextInfo>();

                                AddBattleDamageTextInfo(damageText, BattleDamageTextInfo.TextType.Damage, activeCharacter, damage.ToString(), gs.maxHPStatusID);

                                battleViewer.SetDamageTextInfo(damageText);

                                battleViewer.SetupDamageTextAnimation();

                                isSlipDamage = true;
                            }
                        }
                    }
                    else
                    {
                        foreach (var item in condition.EffectParamSettings.GetAbnormalActionList())
                        {
                            if ((item is Common.Rom.SlipDamageEffectParam effectParam) && (effectParam.Situation == Common.Rom.SlipDamageEffectParam.SituationType.Battle) && (effectParam.SlipDamageCycle != 0) && ((e.damageTurnCount % effectParam.SlipDamageCycle) == 0))
                            {
                                var statusInfo = gs.GetCastStatusParamInfo(effectParam.StatusId);

                                if (statusInfo == null)
                                {
                                    continue;
                                }

                                var consumptionStatusValue = activeCharacter.consumptionStatusValue;
                                var damage = 0;

                                switch (effectParam.SlipDamageType)
                                {
                                    case Common.Rom.Condition.SlipDamageType.Direct:
                                        damage = effectParam.SlipDamageParam;
                                        break;
                                    case Common.Rom.Condition.SlipDamageType.HPPercent:
                                        damage = consumptionStatusValue.GetStatus(statusInfo.guId) * effectParam.SlipDamageParam / 100;
                                        break;
                                    case Common.Rom.Condition.SlipDamageType.MaxHPPercent:
                                        damage = activeCharacter.GetSystemStatus(gameSettings, statusInfo.guId) * effectParam.SlipDamageParam / 100;
                                        break;
                                    default:
                                        break;
                                }

                                if (damage == 0)
                                {
                                    damage = (effectParam.SlipDamageParam > 0) ? 1 : -1;
                                }

                                // キャストのパラメータによる軽減
                                // Mitigation by casting parameters
                                if (damage > 0)
                                {
                                    var poisonDamageReductionPercent = activeCharacter.GetStatus(gameSettings, Rom.GameSettings.PoisonDamageReductionPercentStatusKey);

                                    damage = damage * (100 - poisonDamageReductionPercent) / 100;
                                }

                                var limit = effectParam.SlipDamageLimit;

                                if (0 < damage)
                                {
                                    var useLimit = 0 < limit;
                                    var status = consumptionStatusValue.GetStatus(statusInfo.guId);
                                    var zeroDamage = status <= limit;

                                    if (useLimit && zeroDamage)
                                    {
                                        damage = 0;
                                    }
                                    else if (useLimit || !zeroDamage)
                                    {
                                        consumptionStatusValue.SubStatus(statusInfo.guId, damage);

                                        if (useLimit && (consumptionStatusValue.GetStatus(statusInfo.guId) < limit))
                                        {
                                            // 下限以下になった
                                            // It's below the lower limit
                                            damage = Math.Max(0, damage - limit + consumptionStatusValue.GetStatus(statusInfo.guId));
                                        }
                                    }
                                }
                                else if (damage < 0)
                                {
                                    // 回復時の下限は0
                                    // The lower limit for recovery is 0
                                    limit = 0;

                                    consumptionStatusValue.SubStatus(statusInfo.guId, damage);
                                }

                                if (damage != 0)
                                {
                                    consumptionStatusValue.Max(statusInfo.guId, limit);
                                    consumptionStatusValue.Min(statusInfo.guId, activeCharacter.GetSystemStatus(gameSettings, statusInfo.guId));
                                }

                                switch (statusInfo.Key)
                                {
                                    case Common.Rom.GameSettings.MaxHPStatusKey:
                                        activeCharacter.HitPoint = consumptionStatusValue.GetStatus(statusInfo.guId);
                                        break;
                                    case Common.Rom.GameSettings.MaxMPStatusKey:
                                        activeCharacter.MagicPoint = consumptionStatusValue.GetStatus(statusInfo.guId);
                                        break;
                                    default:
                                        break;
                                }

                                // 毒ダメージで睡眠を解除する場合はコメントアウトを外す
                                // Uncomment out if you want to cancel sleep with poison damage
                                //CheckDamageRecovery(activeCharacter, damage);

                                string message = string.Format(condition.GetMessage(Rom.MessageParam.ConditionForContinueId), activeCharacter.Name);

                                battleViewer.SetDisplayMessage(message);

                                BattleDamageTextInfo.TextType textType;

                                if (damage < 0)
                                {
                                    textType = BattleDamageTextInfo.TextType.Heal;
                                    damage = Math.Abs(damage);
                                }
                                else
                                {
                                    textType = BattleDamageTextInfo.TextType.Damage;
                                }

                                AddBattleDamageTextInfo(damageTextList, textType, activeCharacter, damage.ToString(), statusInfo.guId);

                                isSlipDamage = true;
                            }
                        }
                    }

                    e.damageTurnCount++;
                }

                if (isSlipDamage)
                {
                    battleViewer.SetDamageTextInfo(damageTextList);
                    battleViewer.SetupDamageTextAnimation();

                    statusUpdateTweener.Begin(0, 1.0f, 30);

                    if (owner.debugSettings.battleHpAndMpMax)
                    {
                        foreach (var info in gameSettings.CastStatusParamInfoList)
                        {
                            if (info.Consumption)
                            {
                                activeCharacter.consumptionStatusValue.SetStatus(info.guId, activeCharacter.baseStatusValue.GetStatus(info.guId));
                            }
                        }

                        activeCharacter.HitPoint = activeCharacter.MaxHitPoint;
                        activeCharacter.MagicPoint = activeCharacter.MaxMagicPoint;
                    }

                    SetNextBattleStatus(activeCharacter);

                    ChangeBattleState(BattleState.DisplayStatusDamage);
                }
                else
                {
                    ChangeBattleState(BattleState.CheckBattleCharacterDown2);
                }
            }
            else
            {
                ChangeBattleState(BattleState.CheckBattleCharacterDown2);
            }
        }

        private void UpdateBattleState_CheckBattleCharacterDown2(BattleState nextBattleState)
        {
            ClearCommandEffect();

            CheckBattleCharacterDown();

            ChangeBattleState(nextBattleState);
        }

        private void UpdateBattleState_BattleFinishCheck2()
        {
            var battleResult = CheckBattleFinish();

            if (battleResult == BattleResultState.NonFinish)
            {
                // Speed-based turn system: No need to manually switch turns
                // The next character will be determined by speed in UpdateBattleState_WaitCtbGauge
                
                ResetCtbGauge(activeCharacter);
                ChangeBattleState(WAIT_CTB_GAUGE);
                if (activeCharacter != null)
                    activeCharacter.selectedBattleCommandType = BattleCommandType.Undecided;
                //{
                //    commandExecuteMemberCount++;

                //    if (commandExecuteMemberCount >= battleEntryCharacters.Count)
                //    {
                //        foreach (var player in playerData)
                //        {
                //            player.forceSetCommand = false;
                //            player.commandSelectedCount = 0;
                //            player.selectedBattleCommandType = BattleCommandType.Undecided;
                //        }
                //        foreach (var enemy in enemyData)
                //        {
                //            enemy.selectedBattleCommandType = BattleCommandType.Undecided;
                //        }

                //        battleEvents.start(Rom.Script.Trigger.BATTLE_TURN);
                //        totalTurn++;
                //        ChangeBattleState(BattleState.Wait);
                //    }
                //    else
                //    {
                //        ChangeBattleState(BattleState.ReadyExecuteCommand);
                //    }
                //}
                        }
                    else
                    {
                battleEvents.setBattleResult(battleResult);
                ChangeBattleState(BattleState.StartBattleFinishEvent);
            }
        }

        private void ResetCtbGauge(BattleCharacterBase chr)
        {
            if (chr != null)
            {
                if(chr is ExBattlePlayerData exp && exp.turnGauge >= 1)
                    exp.turnGauge -= 1;
                else if (chr is ExBattleEnemyData exe && exe.turnGauge >= 1)
                    exe.turnGauge -= 1;
            }
        }

        private void UpdateBattleState_PlayerChallengeEscape()
        {
            if (battleStateFrameCount >= 0)
            {

                var escapeCharacter = activeCharacter;
                int escapeSuccessPercent = escapeCharacter.EscapeSuccessBasePercent;

                // 素早さの差が大きいほど成功確率が上がる
                // The greater the speed difference, the higher the chance of success.
                if (escapeSuccessPercent < 100 && enemyData.Count > 0)
                {
                    escapeSuccessPercent += (int)EvalFormula(gameSettings.EscapePercentFormula, activeCharacter, enemyData[0], AttackAttributeType.Empty, null);
                }
                else if (enemyData.Count == 0)
                {
                    // If no enemies left (all captured), escape should be automatic success
                    escapeSuccessPercent = 100;
                }

                if (escapeSuccessPercent < 0) escapeSuccessPercent = 0;
                if (escapeSuccessPercent > 100) escapeSuccessPercent = 100;

                if (escapeSuccessPercent >= battleRandom.Next(100))// 逃走成功 / escape success
                {
                    if (BattleResultEscapeEvents != null)
                    {
                        BattleResultEscapeEvents();
                    }

                    Audio.PlaySound(owner.se.escape);

                    battleViewer.SetDisplayMessage(string.Format(escapeCharacter.EscapeSuccessMessage, escapeCharacter.Name));

                    ChangeBattleState(BattleState.PlayerEscapeSuccess);
                }
                else                                               // 逃走失敗 / escape failure
                {
                    playerEscapeFailedCount++;

                    battleViewer.SetDisplayMessage(gameSettings.glossary.battle_escape_failed);

                    ChangeBattleState(BattleState.PlayerEscapeFail);
                }
            }
        }

        private void UpdateBattleState_StartBattleFinishEvent()
        {
            if (owner.mapScene.isToastVisible() || !battleViewer.IsEffectEndPlay)
            {
                return;
            }

            battleEvents.start(Rom.Script.Trigger.BATTLE_END);
            ChangeBattleState(BattleState.ProcessBattleFinish);
        }

        private void UpdateBattleState_ProcessBattleFinish()
        {
            if (battleEvents.isBusy())
                return;
            battleEvents.clearCurrentProcessingTrigger();

            // バトルイベントでHP/MPを改変している可能性があるため、念の為もう一度適用する。
            // It is possible that HP/MP has been modified in the battle event, so please apply it again just in case.
            ApplyPlayerDataToGameData();

            // Check if this is a victory by re-checking battle status
            var currentBattleResult = CheckBattleFinish();
            if (currentBattleResult == BattleResultState.Win)
            {
                ProcessBattleResult(currentBattleResult);
                // ProcessBattleResult will handle the transition to ResultInit, so we don't continue to FinishFadeOut
                return;
            }

            var guid = owner.data.system.transitionBattleLeave.HasValue ? owner.data.system.transitionBattleLeave.Value : gameSettings.transitionBattleLeave;
            if (catalog.getItemFromGuid(guid) == null)
                fadeScreenColorTweener.Begin(new Color(Color.Black, 0), new Color(Color.Black, 255), 30);
            else
                owner.mapScene.SetWipe(guid);

            ChangeBattleState(BattleState.FinishFadeOut);
        }

        private void UpdateBattleState_FinishFadeOut()
        {
            if (battleEvents.isBusy())
                return;
            battleEvents.clearCurrentProcessingTrigger();

            var guid = owner.data.system.transitionBattleLeave.HasValue ? owner.data.system.transitionBattleLeave.Value : gameSettings.transitionBattleLeave;
            if (catalog.getItemFromGuid(guid) == null)
                fadeScreenColorTweener.Update();

            if (!fadeScreenColorTweener.IsPlayTween && !owner.mapScene.IsWiping())
            {
                // バトルイベントでHP/MPを改変している可能性があるため、念の為もう一度適用する。
                // It is possible that HP/MP has been modified in the battle event, so please apply it again just in case.
                ApplyPlayerDataToGameData();

                owner.mapScene.SetWipe(Guid.Empty);
                fadeScreenColorTweener.Begin(new Color(Color.Black, 255), new Color(Color.Black, 0), 15);

                IsDrawingBattleScene = false;
                ((BattleViewer3D)battleViewer).Hide();

                ChangeBattleState(BattleState.FinishFadeIn);
            }
        }

        private void UpdateBattleState_FinishFadeIn()
        {
            battleEntryCharacters.Clear();

            fadeScreenColorTweener.Update();

            if (!fadeScreenColorTweener.IsPlayTween)
            {
                IsPlayingBattleEffect = false;
            }
        }

        private void SkipPlayerBattleCommand(bool inIsEscape = false)
        {
            foreach (var player in playerData)
            {
                player.selectedBattleCommandType = BattleCommandType.Skip;
            }

            if (inIsEscape)
            {
                //playerData[0].selectedBattleCommandType = BattleCommandType.PlayerEscape;
                activeCharacter.selectedBattleCommandType = BattleCommandType.PlayerEscape;
            }

            ChangeBattleState(BattleState.ReadyExecuteCommand);
        }

        private bool isQualifiedSkillCostItem(BattleCharacterBase activeCharacter, Common.Rom.NSkill skill)
        {
            if (activeCharacter is BattleEnemyData)
                return true;

            return party.isOKToConsumptionItem(skill.option.consumptionItem, skill.option.consumptionItemAmount);
        }

        public override bool isContinuable()
        {
            return BattleResult == BattleResultState.Lose_Continue ||
                   BattleResult == BattleResultState.Escape ||
                   BattleResult == BattleResultState.Lose_Advanced_GameOver;
        }

        private void setAttributeWithWeaponDamage(BattleCharacterBase target, AttackAttributeType attackAttribute, int elementAttack)
        {
            var condition = catalog.getItemFromGuid<Rom.Condition>(attackAttribute);

            if (condition == null)
            {
                return;
            }

            if (target.SetCondition(catalog, attackAttribute, battleEvents, true))
            {
                if (!condition.deadCondition)
                {
                    if (!displayedSetConditionsDic.ContainsKey(target))
                    {
                        displayedSetConditionsDic.Add(target, new List<Rom.Condition>());
                    }

                    // 連続攻撃で、同じ状態が付加されないようにチェック
                    // Check so that the same state is not added in consecutive attacks
                    if (!displayedSetConditionsDic[target].Contains(condition) && target.conditionInfoDic.ContainsKey(attackAttribute))
                    {
                        displayedSetConditionsDic[target].Add(condition);
                    }
                }
                else if (condition.AttachPercent == 0)
                {
                    target.HitPoint = 0;
                    target.consumptionStatusValue.SetStatus(catalog.getGameSettings().maxHPStatusID, 0);
                }
            }
        }

        private List<BattleCharacterBase> createBattleCharacterList()
        {
            var battleCharacters = new List<BattleCharacterBase>();
            battleCharacters.AddRange(targetPlayerData);
            battleCharacters.AddRange(targetEnemyData);
            return battleCharacters;
        }

        private bool isReady3DCamera()
        {
            if (battleViewer is BattleViewer3D)
            {
                var bv3d = battleViewer as BattleViewer3D;
#if false
                return bv3d.getCurrentCameraTag() != BattleCameraController.TAG_FORCE_WAIT;
#else
                return !bv3d.camManager.isPlayAnim || bv3d.camManager.isWaitCameraPlaying || bv3d.camManager.isSkillCameraPlaying(true);
#endif
            }

            return true;
        }

        private bool isReadyActor()
        {
            if (battleViewer is BattleViewer3D)
            {
                var bv3d = battleViewer as BattleViewer3D;
                return bv3d.isActiveCharacterReady();
            }

            return true;
        }


        public bool CheckAndDoReTarget(BattleCharacterBase chr = null)
        {
            if (chr == null)
                chr = activeCharacter;

            if (chr == null)
                return false;

            // 攻撃対象を変える必要があるかチェック
            // Check if the attack target needs to be changed
            if (IsReTarget(chr))
            {
                bool isFriendRecoveryDownStatus = false;

                switch (chr.selectedBattleCommandType)
                {
                    case BattleCommandType.SameSkillEffect:
                        {
                            var skill = catalog.getItemFromGuid(chr.selectedBattleCommand.refGuid) as Rom.NSkill;

                            isFriendRecoveryDownStatus = (skill.friendEffect != null && skill.friendEffect.HasDeadCondition(catalog) && skill.option.onlyForDown);
                        }
                        break;

                    case BattleCommandType.Skill:
                        isFriendRecoveryDownStatus = (
                            chr.selectedSkill.friendEffect != null &&
                            chr.selectedSkill.friendEffect.HasDeadCondition(catalog) &&
                            chr.selectedSkill.option.onlyForDown);
                        break;

                    case BattleCommandType.Item:
                        if (chr.selectedItem.item.expendable != null)
                        {
                            isFriendRecoveryDownStatus = chr.selectedItem.item.expendable.HasRecoveryDeadCondition(catalog);
                        }
                        else if (chr.selectedItem.item.expendableWithSkill != null)
                        {
                            var skill = catalog.getItemFromGuid(chr.selectedItem.item.expendableWithSkill.skill) as Common.Rom.NSkill;

                            isFriendRecoveryDownStatus = (skill.friendEffect != null && skill.friendEffect.HasDeadCondition(catalog) && skill.option.onlyForDown);
                        }
                        break;
                    case BattleCommandType.Position:
                        chr.targetCharacter = null;
                        return false;
                }

                chr.targetCharacter = ReTarget(chr, isFriendRecoveryDownStatus);

                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, chr.Name,
                    string.Format("Retarget / New Target : {0}", chr.targetCharacter.FirstOrDefault(x => x != null)?.Name ?? "Empty"));

                return false;
            }

            return true;
        }

        private void UpdateCommandSelect()
        {
            var battleCommandChoiceWindowDrawer = battleViewer.battleCommandChoiceWindowDrawer;
            var commandTargetWindowDrawer = battleViewer.commandTargetSelector;
            var skillSelectWindowDrawer = battleViewer.skillSelectWindowDrawer;
            var itemSelectWindowDrawer = battleViewer.itemSelectWindowDrawer;
            var stockSelectWindowDrawer = battleViewer.stockSelectWindowDrawer;

            if (commandSelectPlayer != null && commandSelectPlayer.commandTargetList.Count > 0)
            {
                foreach (var target in commandSelectPlayer.commandTargetList) target.IsSelect = false;
            }

            switch (battleCommandState)
            {
                case SelectBattleCommandState.CommandSelect:
                    //battleCommandChoiceWindowDrawer.Update();

                    if (Viewer.ui.command.Decided &&
                        Viewer.ui.command.Index >= 0 &&
                        battleCommandChoiceWindowDrawer.GetChoicesData()[Viewer.ui.command.Index].enable)
                    //if (battleCommandChoiceWindowDrawer.CurrentSelectItemEnable && (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU) || battleCommandChoiceWindowDrawer.decided))
                    {
                        battleCommandChoiceWindowDrawer.saveSelected();
                        battleCommandChoiceWindowDrawer.decided = false;
                        Audio.PlaySound(owner.se.decide);

                        commandSelectPlayer.selectedBattleCommand = playerBattleCommand[Viewer.ui.command.Index];

                        switch (commandSelectPlayer.selectedBattleCommand.type)
                        {
                            case BattleCommand.CommandType.ATTACK: battleCommandState = SelectBattleCommandState.Attack_Command; break;
                            case BattleCommand.CommandType.GUARD: battleCommandState = SelectBattleCommandState.Guard_Command; break;
                            case BattleCommand.CommandType.CHARGE: battleCommandState = SelectBattleCommandState.Charge_Command; break;
                            case BattleCommand.CommandType.SKILL: battleCommandState = SelectBattleCommandState.SkillSameEffect_Command; break;
                            case BattleCommand.CommandType.SKILLMENU: battleCommandState = SelectBattleCommandState.Skill_Command; break;
                            case BattleCommand.CommandType.ITEMMENU: battleCommandState = SelectBattleCommandState.Item_Command; break;
                            case BattleCommand.CommandType.ESCAPE: battleCommandState = SelectBattleCommandState.Escape_Command; break;
                            case BattleCommand.CommandType.BACK: battleCommandState = SelectBattleCommandState.Back_Command; break;
                            case BattleCommand.CommandType.POSITION: battleCommandState = SelectBattleCommandState.Position_MakeTargetList; break;
                        }

                    }

                    else if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.CANCEL, Input.GameState.MENU))
                    {
                        Audio.PlaySound(owner.se.cancel);
                        commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Nothing;
                        battleCommandState = SelectBattleCommandState.CommandCancel;
                    }
                    break;

                // 通常攻撃
                // normal attack
                case SelectBattleCommandState.Attack_Command:

                    {
                        battleCommandState = SelectBattleCommandState.Attack_MakeTargetList;
                    }
                    break;

                case SelectBattleCommandState.Attack_MakeTargetList:
                {
                    commandSelectPlayer.commandTargetList.Clear();
                    commandTargetWindowDrawer.Clear();

                    commandSelectPlayer.commandTargetList.AddRange(targetEnemyData.Where(enemy => (enemy.HitPoint > 0) && IsHitRange(commandSelectPlayer, enemy)));
                    commandTargetWindowDrawer.AddBattleCharacters(commandSelectPlayer.commandTargetList);

                    commandTargetWindowDrawer.ResetSelect(commandSelectPlayer);

                    battleCommandState = SelectBattleCommandState.Attack_SelectTarget;
                }
                break;

                case SelectBattleCommandState.Attack_SelectTarget:
                {
                    bool isDecide = commandTargetWindowDrawer.InputUpdate();

                    if (commandTargetWindowDrawer.Count > 0)
                    {
                        var targetMonster = commandTargetWindowDrawer.CurrentSelectCharacter;

                        targetMonster.IsSelect = true;

                        battleViewer.SetDisplayMessage(targetMonster.Name, WindowType.CommandTargetMonsterListWindow);   // TODO
                    }

                    if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU) || isDecide)
                    {
                        Audio.PlaySound(owner.se.decide);
                        commandTargetWindowDrawer.saveSelect();
                        commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Attack;
                        battleCommandState = SelectBattleCommandState.CommandEnd;
                    }
                    if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.CANCEL, Input.GameState.MENU))
                    {
                        Audio.PlaySound(owner.se.cancel);

                        commandTargetWindowDrawer.Clear();

                        battleViewer.ClearDisplayMessage();
                        battleViewer.OpenWindow(WindowType.PlayerCommandWindow);

                        battleCommandState = SelectBattleCommandState.CommandSelect;
                    }
                    break;
                }

                // 防御
                // defense
                case SelectBattleCommandState.Guard_Command:
                    commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Guard;
                    battleCommandState = SelectBattleCommandState.CommandEnd;
                    break;

                // チャージ
                // charge
                case SelectBattleCommandState.Charge_Command:
                    commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Charge;
                    battleCommandState = SelectBattleCommandState.CommandEnd;
                    break;

                case SelectBattleCommandState.SkillSameEffect_Command:
                    battleCommandState = SelectBattleCommandState.SkillSameEffect_MakeTargetList;
                    break;

                case SelectBattleCommandState.SkillSameEffect_MakeTargetList:
                    commandSelectPlayer.commandTargetList.Clear();
                    commandTargetWindowDrawer.Clear();

                    {
                        var windowType = WindowType.CommandTargetPlayerListWindow;
                        var skill = catalog.getItemFromGuid(commandSelectPlayer.selectedBattleCommand.refGuid) as Rom.NSkill;
                        commandSelectPlayer.selectedItem = null;
                        commandSelectPlayer.selectedSkill = skill;

                        switch (skill.option.target)
                        {
                            case Rom.TargetType.PARTY_ONE:
                            case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                                // Pokemon-style: Include both active and reserve party members for targeting
                                var allPartyMembers = GetAllTargetablePartyMembers();
                                commandSelectPlayer.commandTargetList.AddRange(allPartyMembers.Where(x => IsHitRange(commandSelectPlayer, skill.Range, x)));

                                commandTargetWindowDrawer.AddBattleCharacters(commandSelectPlayer.commandTargetList);

                                commandTargetWindowDrawer.ResetSelect(commandSelectPlayer);

                                battleViewer.OpenWindow(WindowType.CommandTargetPlayerListWindow);
                                battleCommandState = SelectBattleCommandState.SkillSameEffect_SelectTarget;

                                windowType = WindowType.CommandTargetPlayerListWindow;
                                break;

                            case Rom.TargetType.ENEMY_ONE:
                            case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                            case Rom.TargetType.SELF_ENEMY_ONE:
                            case Rom.TargetType.OTHERS_ENEMY_ONE:
                                commandSelectPlayer.commandTargetList.AddRange(targetEnemyData.Where(enemy => (enemy.HitPoint > 0) && IsHitRange(commandSelectPlayer, skill.Range, enemy)));

                                commandTargetWindowDrawer.AddBattleCharacters(commandSelectPlayer.commandTargetList);

                                commandTargetWindowDrawer.ResetSelect(commandSelectPlayer);

                                battleViewer.OpenWindow(WindowType.CommandTargetMonsterListWindow);
                                battleCommandState = SelectBattleCommandState.SkillSameEffect_SelectTarget;

                                windowType = WindowType.CommandTargetMonsterListWindow;
                                break;

                            default:
                                commandSelectPlayer.selectedBattleCommandType = BattleCommandType.SameSkillEffect;
                                battleCommandState = SelectBattleCommandState.CommandEnd;
                                break;
                        }

                        battleViewer.SetDisplayMessage(gameSettings.glossary.battle_target, windowType);   // TODO
                    }
                    break;

                case SelectBattleCommandState.SkillSameEffect_SelectTarget:
                {
                    bool isDecide = commandTargetWindowDrawer.InputUpdate();

                    if (commandTargetWindowDrawer.Count > 0)
                    {
                        commandTargetWindowDrawer.CurrentSelectCharacter.IsSelect = true;
                    }

                    if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU) || isDecide)
                    {
                        Audio.PlaySound(owner.se.decide);
                        commandTargetWindowDrawer.saveSelect();

                        commandSelectPlayer.selectedBattleCommandType = BattleCommandType.SameSkillEffect;
                        battleCommandState = SelectBattleCommandState.CommandEnd;
                    }
                    if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.CANCEL, Input.GameState.MENU))
                    {
                        Audio.PlaySound(owner.se.cancel);

                        commandTargetWindowDrawer.Clear();

                        battleViewer.ClearDisplayMessage();
                        battleViewer.OpenWindow(WindowType.PlayerCommandWindow);

                        battleCommandState = SelectBattleCommandState.CommandSelect;
                    }
                    break;
                }

                // スキル
                // skill
                case SelectBattleCommandState.Skill_Command:
                    battleCommandState = SelectBattleCommandState.Skill_SelectSkill;
                    Viewer.ui.skillList = layoutDic.ContainsKey(commandSelectPlayer.selectedBattleCommand.refGuid) ? layoutDic[commandSelectPlayer.selectedBattleCommand.refGuid] : null;
                    battleViewer.OpenWindow(WindowType.SkillListWindow);

                    skillSelectWindowDrawer.SelectDefaultItem(commandSelectPlayer, battleState);
                    skillSelectWindowDrawer.HeaderTitleIcon = commandSelectPlayer.selectedBattleCommand.icon;
                    skillSelectWindowDrawer.HeaderTitleText = commandSelectPlayer.selectedBattleCommand.name;
                    skillSelectWindowDrawer.FooterTitleIcon = null;
                    skillSelectWindowDrawer.FooterTitleText = "";
                    skillSelectWindowDrawer.FooterSubDescriptionText = "";

                    MakeSkillList();
                    break;

                case SelectBattleCommandState.Skill_SelectSkill:
                    //skillSelectWindowDrawer.FooterMainDescriptionText = "";
                    //skillSelectWindowDrawer.Update();

                    //if (skillSelectWindowDrawer.CurrentSelectItemType == ChoiceWindowDrawer.ItemType.Item && skillSelectWindowDrawer.ChoiceItemCount > 0)
                    //{
                    //    commandSelectPlayer.selectedSkill = commandSelectPlayer.useableSkillList[skillSelectWindowDrawer.CurrentSelectItemIndex];

                    //    skillSelectWindowDrawer.FooterTitleIcon = commandSelectPlayer.selectedSkill.icon;
                    //    skillSelectWindowDrawer.FooterTitleText = commandSelectPlayer.selectedSkill.name;
                    //    skillSelectWindowDrawer.FooterMainDescriptionText = Common.Util.createSkillDescription(gameSettings.glossary, commandSelectPlayer.selectedSkill);
                    //}

                    // 決定
                    // decision
                    if (Viewer.ui.skillList.Decided &&
                        Viewer.ui.skillList.Index >= 0 &&
                        commandSelectPlayer.useableSkillList.Count > Viewer.ui.skillList.Index &&
                        skillSelectWindowDrawer.GetChoicesData()[Viewer.ui.skillList.Index].enable)
                    //if ((Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU) || skillSelectWindowDrawer.decided) && skillSelectWindowDrawer.CurrentSelectItemType == ChoiceWindowDrawer.ItemType.Item && commandSelectPlayer.useableSkillList.Count > 0 && skillSelectWindowDrawer.CurrentSelectItemEnable)
                    {
                        commandSelectPlayer.selectedItem = null;
                        commandSelectPlayer.selectedSkill = commandSelectPlayer.useableSkillList[Viewer.ui.skillList.Index];

                        skillSelectWindowDrawer.saveSelected();
                        skillSelectWindowDrawer.decided = false;
                        Audio.PlaySound(owner.se.decide);

                        battleCommandState = SelectBattleCommandState.Skill_MakeTargetList;
                    }

                    // キャンセル
                    // cancel
                    if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.CANCEL, Input.GameState.MENU))
                    //if (((Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU) || skillSelectWindowDrawer.decided) && skillSelectWindowDrawer.CurrentSelectItemType == ChoiceWindowDrawer.ItemType.Cancel) || Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.CANCEL, Input.GameState.MENU))
                    {
                        skillSelectWindowDrawer.decided = false;
                        Audio.PlaySound(owner.se.cancel);

                        battleViewer.ClearDisplayMessage();
                        battleViewer.OpenWindow(WindowType.PlayerCommandWindow);

                        battleCommandState = SelectBattleCommandState.CommandSelect;
                    }
                    break;

                case SelectBattleCommandState.Skill_MakeTargetList:
                    commandSelectPlayer.commandTargetList.Clear();
                    commandTargetWindowDrawer.Clear();

                    {
                        var windowType = WindowType.CommandTargetPlayerListWindow;

                        switch (commandSelectPlayer.selectedSkill.option.target)
                        {
                            case Rom.TargetType.PARTY_ONE:
                            case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                                // Pokemon-style: Include both active and reserve party members for targeting
                                var allPartyMembersSkill = GetAllTargetablePartyMembers();
                                commandSelectPlayer.commandTargetList.AddRange(allPartyMembersSkill.Where(x => IsHitRange(commandSelectPlayer, commandSelectPlayer.selectedSkill.Range, x)));

                                commandTargetWindowDrawer.AddBattleCharacters(commandSelectPlayer.commandTargetList);

                                commandTargetWindowDrawer.ResetSelect(commandSelectPlayer);

                                battleViewer.OpenWindow(WindowType.CommandTargetPlayerListWindow);
                                battleCommandState = SelectBattleCommandState.Skill_SelectTarget;

                                windowType = WindowType.CommandTargetPlayerListWindow;
                                break;

                            case Rom.TargetType.ENEMY_ONE:
                            case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                            case Rom.TargetType.SELF_ENEMY_ONE:
                            case Rom.TargetType.OTHERS_ENEMY_ONE:
                                commandSelectPlayer.commandTargetList.AddRange(targetEnemyData.Where(enemy => (enemy.HitPoint > 0) && IsHitRange(commandSelectPlayer, commandSelectPlayer.selectedSkill.Range, enemy)));

                                commandTargetWindowDrawer.AddBattleCharacters(commandSelectPlayer.commandTargetList);

                                commandTargetWindowDrawer.ResetSelect(commandSelectPlayer);

                                battleViewer.OpenWindow(WindowType.CommandTargetMonsterListWindow);
                                battleCommandState = SelectBattleCommandState.Skill_SelectTarget;

                                windowType = WindowType.CommandTargetMonsterListWindow;
                                break;

                            default:
                                commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Skill;
                                battleCommandState = SelectBattleCommandState.CommandEnd;
                                break;
                        }

                        battleViewer.SetDisplayMessage(gameSettings.glossary.battle_target, windowType);   // TODO
                    }
                    break;

                case SelectBattleCommandState.Skill_SelectTarget:
                {
                    bool isDecide = commandTargetWindowDrawer.InputUpdate();

                    if (commandTargetWindowDrawer.Count > 0)
                    {
                        var targetCharacter = commandTargetWindowDrawer.CurrentSelectCharacter;
                        targetCharacter.IsSelect = true;
                        
                        // Pokemon-style: Show current selection and total available targets for skills
                        var currentIndex = 0;
                        var targetList = battleViewer.commandTargetSelector.GetTargetList();
                        for (int i = 0; i < targetList.Count; i++)
                        {
                            if (targetList[i] == targetCharacter)
                            {
                                currentIndex = i + 1;
                                break;
                            }
                        }
                        
                        var targetMessage = string.Format("({0}/{1}) {2} - HP: {3}/{4}", 
                            currentIndex, targetList.Count, targetCharacter.Name, 
                            targetCharacter.HitPoint, targetCharacter.MaxHitPoint);
                        battleViewer.SetDisplayMessage(targetMessage, WindowType.CommandTargetPlayerListWindow);

                        if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU) || isDecide)
                        {
                            Audio.PlaySound(owner.se.decide);
                            commandTargetWindowDrawer.saveSelect();

                            commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Skill;
                            battleCommandState = SelectBattleCommandState.CommandEnd;
                        }
                    }

                    if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.CANCEL, Input.GameState.MENU))
                    {
                        Audio.PlaySound(owner.se.cancel);

                        commandTargetWindowDrawer.Clear();

                        battleViewer.OpenWindow(WindowType.SkillListWindow);

                        battleCommandState = SelectBattleCommandState.Skill_Command;
                    }
                    break;
                }

                // アイテム
                // item
                case SelectBattleCommandState.Item_Command:
                    Viewer.ui.itemList = layoutDic.ContainsKey(commandSelectPlayer.selectedBattleCommand.refGuid) ? layoutDic[commandSelectPlayer.selectedBattleCommand.refGuid] : null;

                    itemSelectWindowDrawer.SelectDefaultItem(commandSelectPlayer, battleState);
                    itemSelectWindowDrawer.HeaderTitleIcon = commandSelectPlayer.selectedBattleCommand.icon;
                    itemSelectWindowDrawer.HeaderTitleText = commandSelectPlayer.selectedBattleCommand.name;
                    itemSelectWindowDrawer.FooterTitleIcon = null;
                    itemSelectWindowDrawer.FooterTitleText = "";
                    itemSelectWindowDrawer.FooterSubDescriptionText = "";

                    commandSelectPlayer.haveItemList.Clear();
                    itemSelectWindowDrawer.ClearChoiceListData();

                    var expendableItems = party.Items.Where(itemData => itemData.item.IsExpandable && !itemData.item.IsExpandableWithSkill && itemData.item.expendable.availableInBattle);
                    var skillItems = party.Items.Where(itemData => itemData.item.IsExpandableWithSkill && itemData.item.expendableWithSkill.availableInBattle);
                    var useableItems = expendableItems.Union(skillItems);

                    var filterTagProperties = Viewer.ui.itemList.FilterTagProperties;

                    if ((filterTagProperties?.Count ?? 0) == 0)
                    {
                        commandSelectPlayer.haveItemList.AddRange(useableItems);
                    }
                    else
                    {
                        commandSelectPlayer.haveItemList.AddRange(useableItems.Where(x => ItemFilter.FilterItem(owner, x.item, filterTagProperties)));
                    }

                    foreach (var itemData in commandSelectPlayer.haveItemList)
                    {
                        int itemCount = itemData.num;

                        if (itemData.item.IsExpandable)
                        {
                            // 既にアイテムを使おうとしているメンバーがいたらその分だけ個数を減らす
                            // If there are members already trying to use the item, reduce the number accordingly.
                            itemCount -= (playerData.Count(player => (player != commandSelectPlayer && player.selectedBattleCommandType == BattleCommandType.Item) && (player.selectedItem.item == itemData.item)));
                        }

                        bool useableItem = (itemCount > 0);

                        if (iconTable.ContainsKey(itemData.item.icon.guId))
                        {
                            itemSelectWindowDrawer.AddChoiceData(iconTable[itemData.item.icon.guId], itemData.item.icon, itemData.item.name, itemCount, itemData.item, useableItem);
                        }
                        else
                        {
                            itemSelectWindowDrawer.AddChoiceData(itemData.item.name, itemCount, itemData.item, useableItem);
                        }
                    }

                    battleViewer.OpenWindow(WindowType.ItemListWindow);
                    battleCommandState = SelectBattleCommandState.Item_SelectItem;
                    break;

                case SelectBattleCommandState.Item_SelectItem:
                    //itemSelectWindowDrawer.FooterMainDescriptionText = "";
                    //itemSelectWindowDrawer.Update();

                    //if (itemSelectWindowDrawer.CurrentSelectItemType == ChoiceWindowDrawer.ItemType.Item && itemSelectWindowDrawer.ChoiceItemCount > 0)
                    //{
                    //    commandSelectPlayer.selectedItem = commandSelectPlayer.haveItemList[itemSelectWindowDrawer.CurrentSelectItemIndex];

                    //    itemSelectWindowDrawer.FooterTitleText = commandSelectPlayer.selectedItem.item.name;
                    //    itemSelectWindowDrawer.FooterMainDescriptionText = commandSelectPlayer.selectedItem.item.description;
                    //    //itemSelectWindowDrawer.FooterSubDescriptionText = string.Format("所持数 {0, 4}個", commandSelectPlayer.selectedItem.num);
                    // //itemSelectWindowDrawer.FooterSubDescriptionText = string.Format(\
                    //}

                    // 決定
                    // decision
                    if (Viewer.ui.skillList.Decided &&
                        Viewer.ui.itemList.Index >= 0 &&
                        commandSelectPlayer.haveItemList.Count > Viewer.ui.itemList.Index &&
                        itemSelectWindowDrawer.GetChoicesData()[Viewer.ui.itemList.Index].enable)
                    //if ((Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU) || itemSelectWindowDrawer.decided) && itemSelectWindowDrawer.CurrentSelectItemType == ChoiceWindowDrawer.ItemType.Item && itemSelectWindowDrawer.CurrentSelectItemEnable)
                    {
                        commandSelectPlayer.selectedItem = commandSelectPlayer.haveItemList[Viewer.ui.itemList.Index];
                        commandSelectPlayer.selectedSkill = null;

                        itemSelectWindowDrawer.saveSelected();
                        itemSelectWindowDrawer.decided = false;
                        Audio.PlaySound(owner.se.decide);

                        battleCommandState = SelectBattleCommandState.Item_MakeTargetList;
                    }

                    // キャンセル
                    // cancel
                    if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.CANCEL, Input.GameState.MENU))
                    //if (((Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU) || itemSelectWindowDrawer.decided) && itemSelectWindowDrawer.CurrentSelectItemType == ChoiceWindowDrawer.ItemType.Cancel) || Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.CANCEL, Input.GameState.MENU))
                    {
                        itemSelectWindowDrawer.decided = false;
                        Audio.PlaySound(owner.se.cancel);

                        battleViewer.ClearDisplayMessage();
                        battleViewer.OpenWindow(WindowType.PlayerCommandWindow);

                        battleCommandState = SelectBattleCommandState.CommandSelect;
                    }
                    break;

                case SelectBattleCommandState.Item_MakeTargetList:
                    commandSelectPlayer.commandTargetList.Clear();
                    commandTargetWindowDrawer.Clear();

                    {
                        var item = commandSelectPlayer.selectedItem.item;
                        var windowType = WindowType.CommandTargetPlayerListWindow;

                        // スキルを優先にしないと、消耗品は全てプレイヤーが対象になってしまう
                        // If you don't prioritize skills, all consumables will target the player
                        if (item.IsExpandableWithSkill)
                        {
                            var skill = catalog.getItemFromGuid(item.expendableWithSkill.skill) as Rom.NSkill;

                            if (skill != null)
                            {
                                switch (skill.option.target)
                                {
                                    case Rom.TargetType.PARTY_ONE:
                                    case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                                        // Pokemon-style: Include both active and reserve party members for item targeting
                                        var allPartyMembersItem = GetAllTargetablePartyMembers();
                                        foreach (BattlePlayerData player in allPartyMembersItem)
                                        {
                                            if (player.player.isAvailableItem(item) && IsHitRange(commandSelectPlayer, item, player))
                                            {
                                                commandSelectPlayer.commandTargetList.Add(player);
                                                windowType = WindowType.CommandTargetPlayerListWindow;
                                            }
                                        }

                                        commandTargetWindowDrawer.AddBattleCharacters(commandSelectPlayer.commandTargetList);
                                        break;

                                    case Rom.TargetType.SELF_ENEMY_ONE:
                                    case Rom.TargetType.ENEMY_ONE:
                                    case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                                    case Rom.TargetType.OTHERS_ENEMY_ONE:
                                    {
                                        foreach (var monster in targetEnemyData.Where(enemy => enemy.HitPoint > 0))
                                        {
                                            if (IsHitRange(commandSelectPlayer, item, monster))
                                            {
                                                commandSelectPlayer.commandTargetList.Add(monster);
                                            }
                                            windowType = WindowType.CommandTargetMonsterListWindow;
                                        }

                                        commandTargetWindowDrawer.AddBattleCharacters(commandSelectPlayer.commandTargetList);
                                    }
                                    break;

                                    default:
                                        commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Item;
                                        battleCommandState = SelectBattleCommandState.CommandEnd;
                                        return;
                                }
                            }
                        }
                        else if (item.IsExpandable)
                        {
                            {
                                // Pokemon-style: Include both active and reserve party members for expandable item targeting
                                var allPartyMembersExpandable = GetAllTargetablePartyMembers();
                                foreach (var player in allPartyMembersExpandable)
                                {
                                    player.IsSelectDisabled = !player.player.isAvailableItem(item);
                                }

                                commandSelectPlayer.commandTargetList.AddRange(allPartyMembersExpandable.Where(target => IsHitRange(commandSelectPlayer, item, target)));
                                commandTargetWindowDrawer.AddBattleCharacters(commandSelectPlayer.commandTargetList);
                            }
                        }

                        commandTargetWindowDrawer.ResetSelect(commandSelectPlayer);

                        battleViewer.OpenWindow(windowType);
                        battleCommandState = SelectBattleCommandState.Item_SelectTarget;
                        // Pokemon-style: Show count of available targets
                        var targetMessage = string.Format("{0} (Available: {1})", gameSettings.glossary.battle_target, commandSelectPlayer.commandTargetList.Count);
                        battleViewer.SetDisplayMessage(targetMessage, windowType);
                    }
                    break;

                case SelectBattleCommandState.Item_SelectTarget:
                {
                    bool isDecide = commandTargetWindowDrawer.InputUpdate();

                    if (commandTargetWindowDrawer.Count > 0)
                    {
                        var targetCharacter = commandTargetWindowDrawer.CurrentSelectCharacter;
                        targetCharacter.IsSelect = true;
                        
                        // Pokemon-style: Show current selection and total available targets for items
                        var currentIndex = 0;
                        var targetList = battleViewer.commandTargetSelector.GetTargetList();
                        for (int i = 0; i < targetList.Count; i++)
                        {
                            if (targetList[i] == targetCharacter)
                            {
                                currentIndex = i + 1;
                                break;
                            }
                        }
                        
                        var targetMessage = string.Format("({0}/{1}) {2} - HP: {3}/{4}", 
                            currentIndex, targetList.Count, targetCharacter.Name, 
                            targetCharacter.HitPoint, targetCharacter.MaxHitPoint);
                        battleViewer.SetDisplayMessage(targetMessage, WindowType.CommandTargetPlayerListWindow);
                    }

                    if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU) || isDecide)
                    {
                        if (!(commandTargetWindowDrawer.CurrentSelectCharacter?.IsSelectDisabled ?? true))
                        {
                            Audio.PlaySound(owner.se.decide);
                            commandTargetWindowDrawer.saveSelect();

                            commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Item;
                            battleCommandState = SelectBattleCommandState.CommandEnd;

                            break;
                        }
                    }
                    else if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.CANCEL, Input.GameState.MENU))
                    {
                        Audio.PlaySound(owner.se.cancel);

                        commandTargetWindowDrawer.Clear();

                        battleViewer.OpenWindow(WindowType.ItemListWindow);

                        battleCommandState = SelectBattleCommandState.Item_Command;
                    }
                    break;
                }



                // Pokemon-style switching
                // Pokemon-style party member switching
                case SelectBattleCommandState.Position_MakeTargetList:
                {
                    commandSelectPlayer.commandTargetList.Clear();
                    commandTargetWindowDrawer.Clear();

                    var windowType = WindowType.CommandTargetPlayerListWindow;

                    // Pokemon-style: Show available reserve party members to switch with
                    foreach (var player in stockPlayerData)
                    {
                        // All stock players are available for switching (no conditions like being behind party)
                                player.IsSelectDisabled = false;
                        }

                    // Pokemon-style: Add stock party members (reserve members) as switch targets
                    commandSelectPlayer.commandTargetList.AddRange(stockPlayerData);

                    commandTargetWindowDrawer.AddBattleCharacters(commandSelectPlayer.commandTargetList);

                    commandTargetWindowDrawer.ResetSelect(commandSelectPlayer);

                    battleViewer.OpenWindow(windowType);
                    battleCommandState = SelectBattleCommandState.Position_SelectTarget;
                    battleViewer.SetDisplayMessage("Select party member to switch with:", windowType);
                    
                    // Debug: Log available characters
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Pokemon", 
                        string.Format("Available to switch: {0} characters", stockPlayerData.Count));
                    foreach (var player in stockPlayerData)
                    {
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Pokemon", 
                            string.Format("- {0} (HP: {1}/{2})", player.Name, player.HitPoint, player.MaxHitPoint));
                    }
                }
                break;

                case SelectBattleCommandState.Position_SelectTarget:
                {
                    bool isDecide = commandTargetWindowDrawer.InputUpdate();

                    if (commandTargetWindowDrawer.Count > 0)
                    {
                        var targetCharacter = commandTargetWindowDrawer.CurrentSelectCharacter;
                        targetCharacter.IsSelect = true;
                        
                        // Pokemon-style: Show current selection and total available targets
                        var currentIndex = 0;
                        var targetList = battleViewer.commandTargetSelector.GetTargetList();
                        for (int i = 0; i < targetList.Count; i++)
                        {
                            if (targetList[i] == targetCharacter)
                            {
                                currentIndex = i + 1;
                                break;
                            }
                        }
                        
                        var targetMessage = string.Format("({0}/{1}) {2} - HP: {3}/{4}", 
                            currentIndex, targetList.Count, targetCharacter.Name, 
                            targetCharacter.HitPoint, targetCharacter.MaxHitPoint);
                        battleViewer.SetDisplayMessage(targetMessage, WindowType.CommandTargetPlayerListWindow);
                    }

                    if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.DECIDE, Input.GameState.MENU) || isDecide)
                    {
                        if (!(commandTargetWindowDrawer.CurrentSelectCharacter?.IsSelectDisabled ?? true))
                        {
                            Audio.PlaySound(owner.se.decide);
                            commandTargetWindowDrawer.saveSelect();

                            commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Position;
                            battleCommandState = SelectBattleCommandState.CommandEnd;

                            break;
                        }
                    }
                    else if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.CANCEL, Input.GameState.MENU))
                    {
                        Audio.PlaySound(owner.se.cancel);

                        battleViewer.ClearDisplayMessage();
                        battleViewer.OpenWindow(WindowType.PlayerCommandWindow);

                        battleCommandState = SelectBattleCommandState.CommandSelect;
                    }
                    break;
                }

                // 逃げる
                // run away
                case SelectBattleCommandState.Escape_Command:
                    commandSelectPlayer.selectedBattleCommandType = BattleCommandType.PlayerEscape;
                    battleCommandState = SelectBattleCommandState.CommandEnd;
                    break;

                case SelectBattleCommandState.Back_Command:
                    commandSelectPlayer.selectedBattleCommandType = BattleCommandType.Back;
                    battleCommandState = SelectBattleCommandState.CommandEnd;
                    break;
            }
        }

        public void MakeSkillList()
        {
            var skillSelectWindowDrawer = battleViewer.skillSelectWindowDrawer;

            commandSelectPlayer.useableSkillList.Clear();
            skillSelectWindowDrawer.ClearChoiceListData();

            var filterTagProperties = Viewer.ui.skillList.FilterTagProperties;

            if ((filterTagProperties?.Count ?? 0) == 0)
            {
                commandSelectPlayer.useableSkillList.AddRange(commandSelectPlayer.player.skills);
            }
            else
            {
                commandSelectPlayer.useableSkillList.AddRange(commandSelectPlayer.player.skills.Where(x => ItemFilter.FilterSkillWithUserTag(owner, x, filterTagProperties)));
            }

            Viewer.ui.skillList.FilteredSkills = commandSelectPlayer.useableSkillList;
            Viewer.ui.skillList.FilteredSkillUser = commandSelectPlayer.player;

            foreach (var skill in commandSelectPlayer.useableSkillList)
            {
                bool useableSkill = skill.option.availableInBattle &&
                    IsQualifiedSkillCostStatus(commandSelectPlayer, skill) &&
                    isQualifiedSkillCostItem(commandSelectPlayer, skill);

                if (iconTable.ContainsKey(skill.icon.guId))
                {
                    skillSelectWindowDrawer.AddChoiceData(iconTable[skill.icon.guId], skill.icon, skill.name, useableSkill);
                }
                else
                {
                    skillSelectWindowDrawer.AddChoiceData(skill.name, useableSkill);
                }
            }
        }

        private BattleCharacterBase[] MakeTargetList(Common.Rom.NSkill skill)
        {
            List<BattleCharacterBase> targets = new List<BattleCharacterBase>();

            switch (skill.option.target)
            {
                case Rom.TargetType.ALL:
                    break;
            }

            return targets.ToArray();
        }
        private BattleCharacterBase[] MakeTargetList(Common.Rom.NItem item)
        {
            List<BattleCharacterBase> targets = new List<BattleCharacterBase>();

            if (item.expendable != null)
            {
            }
            else if (item.expendableWithSkill != null)
            {
            }

            return targets.ToArray();
        }

        public override void Draw()
        {
            Graphics.BeginDraw();

            switch (battleState)
            {
                case BattleState.StartFlash:
                    DisplayIdUtil.changeScene(DisplayIdUtil.SceneType.MAP);
                    TransitionUtil.instance.Capture();
                    break;

                case BattleState.WaitFlash:
                case BattleState.StartFadeOut:
                case BattleState.FinishFadeIn:
                    Graphics.DrawFillRect(0, 0, Graphics.ScreenWidth, Graphics.ScreenHeight, fadeScreenColorTweener.CurrentValue.R, fadeScreenColorTweener.CurrentValue.G, fadeScreenColorTweener.CurrentValue.B, fadeScreenColorTweener.CurrentValue.A);
                    break;

                case BattleState.StartFadeIn:
                    DrawBackground();
                    battleViewer.Draw(playerViewData, enemyMonsterViewData);
                    Graphics.DrawFillRect(0, 0, Graphics.ScreenWidth, Graphics.ScreenHeight, fadeScreenColorTweener.CurrentValue.R, fadeScreenColorTweener.CurrentValue.G, fadeScreenColorTweener.CurrentValue.B, fadeScreenColorTweener.CurrentValue.A);
                    break;

                case BattleState.SetFinishEffect:
                case BattleState.FinishFadeOut:
                    DrawBackground();
                    if (!owner.IsBattle2D)
                        ((BattleViewer3D)battleViewer).DrawField(playerViewData, enemyMonsterViewData);
                    //resultViewer.Draw();
                    if (BattleResult != BattleResultState.Win)
                        battleViewer.Draw(playerViewData, enemyMonsterViewData);
                    Viewer.DrawResult(false, playerData, resultProperty);
                    Graphics.DrawFillRect(0, 0, Graphics.ScreenWidth, Graphics.ScreenHeight, fadeScreenColorTweener.CurrentValue.R, fadeScreenColorTweener.CurrentValue.G, fadeScreenColorTweener.CurrentValue.B, fadeScreenColorTweener.CurrentValue.A);
                    break;

                case BattleState.ResultInit:
                    DrawBackground();
                    if (!owner.IsBattle2D)
                        ((BattleViewer3D)battleViewer).DrawField(playerViewData, enemyMonsterViewData);
                    break;
                case BattleState.Result:
                    DrawBackground();
                    if (!owner.IsBattle2D)
                        ((BattleViewer3D)battleViewer).DrawField(playerViewData, enemyMonsterViewData);
                    Viewer.DrawResult(true, playerData, resultProperty);
                    //resultViewer.Draw();
                    break;

                default:
                    DrawBackground();
                    battleViewer.Draw(playerViewData, enemyMonsterViewData);
                    break;
            }


            Graphics.EndDraw();

            battleEvents?.Draw();
        }

        private void DrawBackground()
        {
            if (!owner.IsBattle2D)
                return;

            // 背景表示
            // background display
            switch (backGroundStyle)
            {
                case BackGroundStyle.FillColor:
                    Graphics.DrawFillRect(0, 0, Graphics.ScreenWidth, Graphics.ScreenHeight, backGroundColor.R, backGroundColor.G, backGroundColor.B, backGroundColor.A);
                    break;

                case BackGroundStyle.Image:
                    if (openingBackgroundImageScaleTweener.IsPlayTween)
                    {
                        Graphics.DrawImage(backGroundImageId, new Rectangle((int)(Graphics.ScreenWidth / 2 - Graphics.GetImageWidth(backGroundImageId) * openingBackgroundImageScaleTweener.CurrentValue / 2), (int)(Graphics.ScreenHeight / 2 - Graphics.GetImageHeight(backGroundImageId) * openingBackgroundImageScaleTweener.CurrentValue / 2), (int)(Graphics.GetImageWidth(backGroundImageId) * openingBackgroundImageScaleTweener.CurrentValue), (int)(Graphics.GetImageHeight(backGroundImageId) * openingBackgroundImageScaleTweener.CurrentValue)), new Rectangle(0, 0, Graphics.GetImageWidth(backGroundImageId), Graphics.GetImageHeight(backGroundImageId)));
                    }
                    else
                    {
                        Graphics.DrawImage(backGroundImageId, 0, 0);
                    }
                    break;

                case BackGroundStyle.Model:
                    break;
            }
        }

        private void ChangeBattleState(BattleState nextBattleState)
        {
            //GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "System", "Set State to : " + nextBattleState.ToString());
            battleStateFrameCount = 0;

            prevBattleState = battleState;
            battleState = nextBattleState;
        }

        public BattleResultState CheckBattleFinish()
        {
            var resultState = BattleResultState.NonFinish;

            // 戦闘終了条件
            // Combat end condition
            // 1.全ての敵がHP0なったら     -> 勝利 (イベント戦として倒してはいけない敵が登場するならゲームオーバーになる場合もありえる?)
            // 1. When all enemies have 0 HP -\u003e Victory
            // 2.全ての味方がHP0になったら -> 敗北 (ゲームオーバー or フィールド画面に戻る(イベント戦のような特別な戦闘を想定))
            // 2. When all allies' HP reaches 0 -\u003e Defeat
            // 3.「逃げる」コマンドの成功  -> 逃走
            // 3. \
            // 4.その他 強制的に戦闘を終了するスクリプト (例 HPが半分になったらイベントが発生し戦闘終了)
            // 4.Other Scripts to forcibly end the battle (ex. When the HP becomes half, an event occurs and the battle ends)
            // 「発動後に自分が戦闘不能になる」スキルなどによってプレイヤーとモンスターが同時に全滅した場合(条件の1と2を同時に満たす場合)は敗北扱いとする
            // If the player and monsters are annihilated at the same time by a skill such as \


            // Capture mechanic: Process captured enemies first
            ProcessCapturedEnemies();

            // 敵が全て倒れたら（または捕獲されたら）
            // when all the enemies fall (or are captured)
            var aliveEnemyCount = enemyData.Where(monster => monster.HitPoint > 0).Count() + stockEnemyData.Where(monster => monster.HitPoint > 0).Count();
            if (aliveEnemyCount == 0)
            {
                resultState = BattleResultState.Win;
                
                // Log capture information
                if (capturedEnemies.Count > 0)
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                        string.Format("Battle won with {0} enemies captured!", capturedEnemies.Count));
                }
            }

            // 味方のHPが全員0になったら
            // When all allies' HP reaches 0
            if ((playerData.Where(player => player.HitPoint > 0).Count() + stockPlayerData.Where(player => player.HitPoint > 0).Count()) == 0)
            {
                if (gameoverOnLose)
                {
                    if (owner.data.start.gameOverSettings.gameOverType != GameOverSettings.GameOverType.DEFAULT)
                    {
                        resultState = BattleResultState.Lose_Advanced_GameOver;
                    }
                    else
                    {
                        resultState = BattleResultState.Lose_GameOver;
                    }
                }
                else
                {
                    resultState = BattleResultState.Lose_Continue;
                }
            }

            // バトルイベント側で指定されているか？
            // Is it specified on the battle event side?
            battleEvents.checkForceBattleFinish(ref resultState);

            return resultState;
        }

        private void ProcessBattleResult(BattleResultState battleResultState)
        {
            switch (battleResultState)
            {
                case BattleResultState.Win:
                {
                    // Pokemon-style: Bring all party members onto the battlefield for victory celebration
                    BringAllPartyMembersToField();
                    
                    // 経験値とお金とアイテムを加える
                    // Add experience points, money and items
                    int totalMoney = 0;
                    int totalExp = 0;
                    var dropItems = new List<Rom.NItem>();

                    if (itemRate > 0)
                    {
                        var defeatEnemyDatas = new List<BattleEnemyData>(enemyData.Count + stockEnemyData.Count);

                        defeatEnemyDatas.AddRange(enemyData);
                        defeatEnemyDatas.AddRange(stockEnemyData);

                        foreach (var monsterData in enemyData)
                        {

                            var monster = monsterData.monster;
							// Pokemon-style
							var monsterLevel = monsterData.monsterGameData?.level ?? 1;
                            var Random = GetRandom(battleRandom, 1.1f, 0.9f);

                            totalMoney += (int)(monster.money * monsterLevel * 4);
                            totalExp += (int)((monster.exp * monsterLevel) / 7);

                            // アイテム抽選
                            // Item lottery
                            foreach (var dropItem in monster.dropItems)
                            {
                                if (dropItem.item != Guid.Empty)
                                {
                                    var item = catalog.getItemFromGuid(dropItem.item) as Common.Rom.NItem;

                                    if ((item != null) && (battleRandom.Next(100) < dropItem.percent * itemRate / 100))
                                    {
                                        if (item.useEnhance && (item.baseItem == null))
                                        {
                                            item = item.CreateEnhancedItem();

                                            catalog.addEnhancedItem(item);
                                        }

                                        if (catalog.getItemFromGuid<Rom.Event>(item.scriptOnNew) is Rom.Event ev)
                                        {
                                            battleEvents.AddEvent(ev, item);
                                        }

                                        dropItems.Add(item);
                                    }
                                }
                            }
                        }
                    }

                    // Pokemon-style: Include ALL party members (active + reserves) for experience distribution
                    var allPartyMembers = new List<BattlePlayerData>();
                    allPartyMembers.AddRange(playerViewData);  // Active Pokemon
                    allPartyMembers.AddRange(stockPlayerData); // Reserve Pokemon
                    
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Experience", 
                        string.Format("Distributing {0} exp to {1} party members (active: {2}, reserve: {3})", 
                        totalExp, allPartyMembers.Count, playerViewData.Count, stockPlayerData.Count));
                    
                    resultViewer.SetResultData(allPartyMembers.ToArray(), totalExp, totalMoney, dropItems.ToArray(), party.ItemDict);

                    this.rewards.GetExp = totalExp;
                    this.rewards.DropMoney = totalMoney;
                    this.rewards.DropItems = dropItems.ToArray();


                    if (BattleResultWinEvents != null)
                    {
                        BattleResultWinEvents();
                    }
                    
                    // Show capture success message
                    if (capturedEnemies.Count > 0)
                    {
                        string captureMessage = string.Format("Successfully captured {0} enemy{1}!", 
                            capturedEnemies.Count, capturedEnemies.Count > 1 ? "ies" : "y");
                        
                        var capturedNames = string.Join(", ", capturedEnemies.Select(e => e.Name));
                        battleViewer.SetDisplayMessage(captureMessage + "\n" + capturedNames);
                        
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Capture", 
                            string.Format("Victory with captures: {0}", capturedNames));
                    }
                }

                resultViewer.Start();
                ChangeBattleState(BattleState.ResultInit);
                break;

                case BattleResultState.Lose_GameOver:
                {
                    if (BattleResultLoseGameOverEvents != null)
                    {
                        BattleResultLoseGameOverEvents();
                    }
                }
                BattleResult = BattleResultState.Lose_GameOver;
                ChangeBattleState(BattleState.SetFinishEffect);
                break;

                case BattleResultState.Escape_ToTitle:
                    BattleResult = BattleResultState.Escape_ToTitle;
                    ChangeBattleState(BattleState.SetFinishEffect);
                    break;

                case BattleResultState.Lose_Continue:
                    BattleResult = BattleResultState.Lose_Continue;
                    ChangeBattleState(BattleState.SetFinishEffect);
                    break;

                case BattleResultState.Lose_Advanced_GameOver:
                    BattleResult = BattleResultState.Lose_Advanced_GameOver;
                    ChangeBattleState(BattleState.SetFinishEffect);
                    break;

                case BattleResultState.NonFinish:
                    // イベントで敵や味方を増やすなどして、戦闘終了条件を満たさなくなった場合にここに来るので、普通に次のターンにする
                    // If you increase the number of enemies and allies in the event and the battle end conditions are no longer met, you will come here, so normally you will make it the next turn.
                    ChangeBattleState(BattleState.ProcessPoisonStatus);

                    // イベントによりバトル継続になった場合、全員行動済みとしておく
                    // If the battle continues due to an event, all players will be treated as having completed their actions.
                    commandExecuteMemberCount = battleEntryCharacters.Count;

                    // バトル終了イベント進行中フラグを下げる
                    // Lowers the battle end event in progress flag
                    battleEvents.isBattleEndEventStarted = false;
                    break;
            }
        }

        private void ApplyPlayerDataToGameData()
        {
            // Pokemon-style: Apply battle data to ALL party members (active + reserves)
            for (int i = 0; i < playerData.Count; i++)
            {
                ApplyPlayerDataToGameData(playerData[i]);
            }
            
            // Also apply to reserve party members  
            for (int i = 0; i < stockPlayerData.Count; i++)
            {
                ApplyPlayerDataToGameData(stockPlayerData[i]);
            }
            
            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "PostBattle", 
                string.Format("Applied battle data to {0} active + {1} reserve party members", 
                playerData.Count, stockPlayerData.Count));
        }

        internal void ApplyPlayerDataToGameData(BattlePlayerData battlePlayerData)
        {
            var gameData = battlePlayerData.player;

            if (!owner.debugSettings.battleHpAndMpMax)
            {
                foreach (var info in Catalog.sInstance.getGameSettings().CastStatusParamInfoList)
                {
                    if (info.Consumption)
                    {
                        gameData.consumptionStatusValue.SetStatus(info.guId, battlePlayerData.consumptionStatusValue.GetStatus(info.guId));
                    }
                }

                gameData.hitpoint = battlePlayerData.HitPoint;
                gameData.magicpoint = battlePlayerData.MagicPoint;
            }

            // まずはすべて適用する
            // apply all first
            gameData.conditionInfoDic.Clear();
            foreach (var e in battlePlayerData.conditionInfoDic)
            {
                gameData.conditionInfoDic.Add(e.Key, e.Value);
            }
            gameData.refreshConditionEffect();

            // 報酬系データの計算
            // Calculation of reward system data
            gameData.conditionAddExpRateForResult = gameData.conditionAddExpRate;
            itemRate = party.CalcConditionDropItemRate();
            moneyRate = party.CalcConditionRewardRate();

            // バトル終了時に解除される状態の解除と解除時付与等で付与された状態の追加
            // Addition of statuses that are canceled at the end of a battle and statuses that are granted upon cancellation, etc.
            foreach (var e in battlePlayerData.conditionInfoDic)
            {
                if ((e.Value.recovery & Hero.ConditionInfo.RecoveryType.BattleFinished) != 0)
                {
                    gameData.RecoveryCondition(catalog, e.Key, Rom.Condition.RecoveryType.BattleFinished);
                }
				else if (!gameData.conditionInfoDic.ContainsKey(e.Key))
				{
                    gameData.conditionInfoDic.Add(e.Key, e.Value);
                }
            }

            gameData.refreshConditionEffect();
        }

        private BattleCharacterBase GetAttackConditionTargetCharacter(BattleCharacterBase inCharacter)
        {
            foreach (var e in inCharacter.conditionInfoDic)
            {
                var condition = e.Value.rom;

                if (condition != null)
                {
                    Common.Rom.TargetType attackTarget;

                    if (condition.GetAutoAttackParam(out attackTarget))
                    {
                        var tempTargets = new List<BattleCharacterBase>();

                        switch (attackTarget)
                        {
                            case Rom.TargetType.PARTY_ALL:
                                tempTargets.AddRange(inCharacter.FriendPartyRefMember.Where(player => player.HitPoint > 0));
                                break;
                            case Rom.TargetType.ENEMY_ALL:
                                tempTargets.AddRange(inCharacter.EnemyPartyRefMember.Where(enemy => enemy.HitPoint > 0));
                                break;
                            case Rom.TargetType.SELF:
                                tempTargets.Add(inCharacter);
                                break;
                            case Rom.TargetType.OTHERS:
                                tempTargets.AddRange(inCharacter.FriendPartyRefMember.Where(player => (player != inCharacter) && player.HitPoint > 0));
                                break;
                            case Rom.TargetType.ALL:
                                tempTargets.AddRange(inCharacter.FriendPartyRefMember.Where(player => player.HitPoint > 0));
                                tempTargets.AddRange(inCharacter.EnemyPartyRefMember.Where(enemy => enemy.HitPoint > 0));
                                break;
                            case Rom.TargetType.OTHERS_ALL:
                                tempTargets.AddRange(inCharacter.FriendPartyRefMember.Where(player => (player != inCharacter) && player.HitPoint > 0));
                                tempTargets.AddRange(inCharacter.EnemyPartyRefMember.Where(enemy => enemy.HitPoint > 0));
                                break;
                            default:
                                break;
                        }

                        var targets = new List<BattleCharacterBase>();

                        targets.AddRange(tempTargets.Where(enemy => IsHitRange(inCharacter, enemy)));

                        if (targets.Count > 0)
                        {
                            return targets.ElementAt(battleRandom.Next(targets.Count()));
                        }

                        break;
                    }
                }
            }

            return null;
        }

        internal bool IsHitRange(BattleCharacterBase inCharacter, float inRange, BattleCharacterBase inTarget)
        {
            var gs = Catalog.sInstance.getGameSettings();

            if (!gs.useRange || (inRange == 0))
            {
                return true;
            }

            float distance;

            if (gs.checkRange == Rom.GameSettings.CheckRangeType.Line)
            {
                // Z行チェックの時の射程は１マス加算
                // When checking the Z line, the range is added by 1 square.
                inRange++;

                distance = Math.Abs(inCharacter.moveTargetPos.Z - inTarget.moveTargetPos.Z);
            }
            else
            {
                inRange = inRange * inRange;

                var diffX = inCharacter.moveTargetPos.X - inTarget.moveTargetPos.X;
                var diffZ = inCharacter.moveTargetPos.Z - inTarget.moveTargetPos.Z;

                distance = (diffX * diffX + diffZ * diffZ);
            }

            return (distance <= inRange);
        }

        internal bool IsHitRange(BattleCharacterBase inCharacter, BattleCharacterBase inTarget)
        {
            var hero = inCharacter.Hero;
            var weapon = hero.equipments[Hero.WEAPON_INDEX];
            var range = (weapon == null) ? hero.rom.range : weapon.range;

            return IsHitRange(inCharacter, range, inTarget);
        }

        internal bool IsHitRange(BattleCharacterBase inCharacter, Common.Rom.NItem inItem, BattleCharacterBase inTarget)
        {
            return IsHitRange(inCharacter, inItem.range, inTarget);
        }

        internal bool IsHit(BattleCharacterBase inCharacter, BattleCharacterBase inTarget)
        {
            if (!IsHitRange(inCharacter, inTarget))
            {
                return false;
            }

            return inCharacter.Dexterity * (100 - inTarget.Evasion) > battleRandom.Next(100 * 100);
        }

        internal BattleCharacterBase[] GetTargetCharacters(BattleCharacterBase character)
        {
            var targets = new List<BattleCharacterBase>();

            switch (character.selectedBattleCommandType)
            {
                case BattleCommandType.Attack:
                case BattleCommandType.Critical:
                case BattleCommandType.ForceCritical:
                case BattleCommandType.Miss:
                {
                    var attackConditionTarget = GetAttackConditionTargetCharacter(character);

                    if (attackConditionTarget != null)
                    {
                        targets.Add(attackConditionTarget);
                    }
                    else
                    {
                        targets.Add(battleViewer.commandTargetSelector.CurrentSelectCharacter);
                    }

                    if (character.selectedBattleCommandType == BattleCommandType.Critical)
                    {
                        // 必中ではないクリティカルが攻撃に失敗したらミス
                        // If a critical attack fails, it is a miss.
                        character.selectedBattleCommandType = IsHit(character, targets[0]) ? BattleCommandType.ForceCritical : BattleCommandType.Miss;
                    }
                }
                break;

                case BattleCommandType.Charge:
                    targets.Add(character);
                    break;

                case BattleCommandType.Guard:
                    targets.Add(character);
                    break;

                case BattleCommandType.SameSkillEffect:
                {
                    var skill = catalog.getItemFromGuid(character.selectedBattleCommand.refGuid) as Rom.NSkill;

                    switch (skill.option.target)
                    {
                        case Rom.TargetType.PARTY_ONE:
                        case Rom.TargetType.ENEMY_ONE:
                        case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                        case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                        case Rom.TargetType.SELF_ENEMY_ONE:
                        case Rom.TargetType.OTHERS_ENEMY_ONE:
                            targets.Add(battleViewer.commandTargetSelector.CurrentSelectCharacter);
                            break;
                        case Rom.TargetType.ALL:
                            if (character.selectedSkill.friendEffect.HasDeadCondition(catalog) && character.selectedSkill.option.onlyForDown)
                            {
                                targets.AddRange(character.FriendPartyRefMember);
                            }
                            else
                            {
                                targets.AddRange(character.FriendPartyRefMember.Where(member => member.HitPoint > 0));
                            }
                            targets.AddRange(character.EnemyPartyRefMember.Where(enemy => enemy.HitPoint > 0));
                            break;

                        case Rom.TargetType.PARTY_ALL:
                            if (skill.friendEffect.HasDeadCondition(catalog) && skill.option.onlyForDown)
                            {
                                targets.AddRange(character.FriendPartyRefMember);
                            }
                            else
                            {
                                targets.AddRange(character.FriendPartyRefMember.Where(member => member.HitPoint > 0));
                            }
                            break;

                        case Rom.TargetType.ENEMY_ALL:
                            targets.AddRange(character.EnemyPartyRefMember.Where(enemy => enemy.HitPoint > 0));
                            break;

                        case Rom.TargetType.SELF:
                        case Rom.TargetType.SELF_ENEMY_ALL:
                            targets.Add(character);
                            break;

                        case Rom.TargetType.OTHERS:
                        case Rom.TargetType.OTHERS_ALL:
                            if (skill.friendEffect.HasDeadCondition(catalog) && skill.option.onlyForDown)
                            {
                                targets.AddRange(character.FriendPartyRefMember.Where(member => character != member));
                            }
                            else
                            {
                                targets.AddRange(character.FriendPartyRefMember.Where(member => character != member && member.HitPoint > 0));
                            }
                            break;
                    }
                }
                break;

                case BattleCommandType.Skill:
                    switch (character.selectedSkill.option.target)
                    {
                        case Rom.TargetType.PARTY_ONE:
                        case Rom.TargetType.ENEMY_ONE:
                        case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                        case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                        case Rom.TargetType.SELF_ENEMY_ONE:
                        case Rom.TargetType.OTHERS_ENEMY_ONE:
                            targets.Add(battleViewer.commandTargetSelector.CurrentSelectCharacter);
                            break;

                        case Rom.TargetType.ALL:
                            if (character.selectedSkill.friendEffect.HasDeadCondition(catalog) && character.selectedSkill.option.onlyForDown)
                            {
                                targets.AddRange(character.FriendPartyRefMember);
                            }
                            else
                            {
                                targets.AddRange(character.FriendPartyRefMember.Where(member => member.HitPoint > 0));
                            }
                            targets.AddRange(character.EnemyPartyRefMember.Where(enemy => enemy.HitPoint > 0));
                            break;

                        case Rom.TargetType.PARTY_ALL:
                            if (character.selectedSkill.friendEffect.HasDeadCondition(catalog) && character.selectedSkill.option.onlyForDown)
                            {
                                targets.AddRange(character.FriendPartyRefMember);
                            }
                            else
                            {
                                targets.AddRange(character.FriendPartyRefMember.Where(member => member.HitPoint > 0));
                            }
                            break;

                        case Rom.TargetType.ENEMY_ALL:
                            targets.AddRange(character.EnemyPartyRefMember.Where(enemy => enemy.HitPoint > 0));
                            break;

                        case Rom.TargetType.SELF:
                        case Rom.TargetType.SELF_ENEMY_ALL:
                            targets.Add(character);
                            break;

                        case Rom.TargetType.OTHERS:
                        case Rom.TargetType.OTHERS_ALL:
                            if (character.selectedSkill.friendEffect.HasDeadCondition(catalog) && character.selectedSkill.option.onlyForDown)
                            {
                                targets.AddRange(character.FriendPartyRefMember.Where(member => character != member));
                            }
                            else
                            {
                                targets.AddRange(character.FriendPartyRefMember.Where(member => character != member && member.HitPoint > 0));
                            }
                            break;
                    }

                    break;

                case BattleCommandType.Item:
                    if (character.selectedItem.item.IsExpandable)
                    {
                        targets.Add(battleViewer.commandTargetSelector.CurrentSelectCharacter);
                    }
                    else if (character.selectedItem.item.IsExpandableWithSkill)
                    {
                        var skill = (Common.Rom.NSkill)catalog.getItemFromGuid(character.selectedItem.item.expendableWithSkill.skill);

                        if (skill != null)
                        {
                            switch (skill.option.target)
                            {
                                case Rom.TargetType.PARTY_ONE:
                                case Rom.TargetType.ENEMY_ONE:
                                case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                                case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                                case Rom.TargetType.SELF_ENEMY_ONE:
                                    targets.Add(battleViewer.commandTargetSelector.CurrentSelectCharacter);
                                    break;

                                case Rom.TargetType.ALL:
                                    if (character.selectedSkill.friendEffect.HasDeadCondition(catalog) && character.selectedSkill.option.onlyForDown)
                                    {
                                        targets.AddRange(character.FriendPartyRefMember);
                                    }
                                    else
                                    {
                                        targets.AddRange(character.FriendPartyRefMember.Where(member => member.HitPoint > 0));
                                    }
                                    targets.AddRange(character.EnemyPartyRefMember.Where(enemy => enemy.HitPoint > 0));
                                    break;

                                case Rom.TargetType.PARTY_ALL:
                                    if (skill.friendEffect.HasDeadCondition(catalog) && skill.option.onlyForDown)
                                    {
                                        targets.AddRange(character.FriendPartyRefMember);
                                    }
                                    else
                                    {
                                        targets.AddRange(character.FriendPartyRefMember.Where(member => member.HitPoint > 0));
                                    }
                                    break;

                                case Rom.TargetType.ENEMY_ALL:
                                    targets.AddRange(character.EnemyPartyRefMember.Where(enemy => enemy.HitPoint > 0));
                                    break;

                                case Rom.TargetType.SELF:
                                case Rom.TargetType.SELF_ENEMY_ALL:
                                    targets.Add(character);
                                    break;

                                case Rom.TargetType.OTHERS:
                                case Rom.TargetType.OTHERS_ALL:
                                    if (skill.friendEffect.HasDeadCondition(catalog) && skill.option.onlyForDown)
                                    {
                                        targets.AddRange(character.FriendPartyRefMember.Where(member => character != member));
                                    }
                                    else
                                    {
                                        targets.AddRange(character.FriendPartyRefMember.Where(member => character != member && member.HitPoint > 0));
                                    }
                                    break;
                            }
                        }
                    }
                    break;

                case BattleCommandType.Position:
                    targets.Add(battleViewer.commandTargetSelector.CurrentSelectCharacter);
                    break;

                case BattleCommandType.Nothing:
                    //character.targetCharacter = new BattleCharacterBase[ 0 ];
                    break;
            }

            return targets.ToArray();
        }

        private bool IsReTarget(BattleCharacterBase character)
        {
            bool isRetarget = false;

            switch (character.selectedBattleCommandType)
            {
                case BattleCommandType.Attack:
                case BattleCommandType.Critical:
                case BattleCommandType.ForceCritical:
                case BattleCommandType.Miss:
                    // 状態異常時に、行動指定「何もしない」を行うと、targetがnullになっていることがある
                    // When the status is abnormal, if you specify the action \
                    if (character.targetCharacter == null)
                    {
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is Empty.");
                        isRetarget = true;
                        break;
                    }
                    foreach (var target in character.targetCharacter)
                    {
                        if (IsNotActiveTarget(target) || target.IsDeadCondition())
                        {
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is Already Extinct.");
                            isRetarget = true;
                            break;
                        }
                        else if(!IsHitRange(character, target))
                        {
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is So Far.");
                            isRetarget = true;
                            break;
                        }

                        // 敵の場合は「狙われやすさ」がセットされていたら必ず再抽選する
                        // For enemies, if the \
                        else if(character is BattleEnemyData && character.EnemyPartyRefMember.Exists(x => x.HateCondition != 0))
                        {
                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : To Apply Hate Condition.");
                            isRetarget = true;
                            break;
                        }
                    }
                    break;

                case BattleCommandType.SameSkillEffect:
                    foreach (var target in character.targetCharacter)
                    {
                        var skill = catalog.getItemFromGuid(character.selectedBattleCommand.refGuid) as Rom.NSkill;

                        switch (skill.option.target)
                        {
                            case Rom.TargetType.ENEMY_ONE:
                            case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                            case Rom.TargetType.SELF_ENEMY_ONE:
                            case Rom.TargetType.OTHERS_ENEMY_ONE:
                                if (skill.enemyEffect != null)
                                {
                                    if (IsNotActiveTarget(target) || target.IsDeadCondition())
                                    {
                                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is Already Extinct.");
                                        isRetarget = true;
                                    }
                                    else if(!IsHitRange(character, skill.Range, target))
                                    {
                                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is So Far.");
                                        isRetarget = true;
                                    }
                                }
                                break;

                            case Rom.TargetType.PARTY_ONE:
                            case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                                if (skill.friendEffect != null)
                                {
                                    if (IsNotActiveTarget(target))
                                    {
                                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is Already Extinct.");
                                        isRetarget = true;
                                    }
                                    else if (!IsHitRange(character, skill.Range, target))
                                    {
                                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is So Far.");
                                        isRetarget = true;
                                    }
                                    // 蘇生効果ありで対象が死んでいないか戦闘不能者のみの場合は無効
                                    // Invalid if there is a resurrection effect and the target is not dead or only incapacitated
                                    else if (skill.friendEffect.HasDeadCondition(catalog))
                                    {
                                        if (!target.IsDeadCondition() && skill.IsOnlyForDown(catalog))
                                        {
                                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is Alive.");
                                            isRetarget = true;
                                        }
                                    }
                                    else
                                    {
                                        if (target.IsDeadCondition())
                                        {
                                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is Dead.");
                                            isRetarget = true;
                                        }
                                    }
                                }
                                break;
                        }
                    }
                    break;

                case BattleCommandType.Skill:
                    foreach (var target in character.targetCharacter)
                    {
                        var skill = character.selectedSkill;

                        switch (skill.option.target)
                        {
                            case Rom.TargetType.ENEMY_ONE:
                            case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                            case Rom.TargetType.SELF_ENEMY_ONE:
                            case Rom.TargetType.OTHERS_ENEMY_ONE:
                                if (skill.enemyEffect != null)
                                {
                                    if (IsNotActiveTarget(target) || target.IsDeadCondition())
                                    {
                                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is Already Extinct.");
                                        isRetarget = true;
                                    }
                                    else if(character.EnemyPartyRefMember.Exists(x => x.HateCondition != 0))
                                    {
                                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : To Apply Hate Condition.");
                                        isRetarget = true;
                                    }
                                    else if(!IsHitRange(character, skill.Range, target))
                                    {
                                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is So Far.");
                                        isRetarget = true;
                                    }
                                }
                                break;

                            case Rom.TargetType.PARTY_ONE:
                            case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                                if (skill.friendEffect != null)
                                {
                                    if (IsNotActiveTarget(target))
                                    {
                                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is Already Extinct.");
                                        isRetarget = true;
                                    }
                                    else if(!IsHitRange(character, skill.Range, target))
                                    {
                                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is So Far.");
                                        isRetarget = true;
                                    }
                                    // 蘇生効果ありで対象が死んでいないか戦闘不能者のみの場合は無効
                                    // Invalid if there is a resurrection effect and the target is not dead or only incapacitated
                                    else if (skill.friendEffect.HasDeadCondition(catalog))
                                    {
                                        if (!target.IsDeadCondition() && skill.IsOnlyForDown(catalog))
                                        {
                                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is Alive.");
                                            isRetarget = true;
                                        }
                                    }
                                    else
                                    {
                                        if (target.IsDeadCondition())
                                        {
                                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is Dead.");
                                            isRetarget = true;
                                        }
                                    }
                                }
                                break;
                        }
                    }
                    break;

                case BattleCommandType.Item:
                    foreach (var target in character.targetCharacter)
                    {
                        if (character.selectedItem.item.expendableWithSkill != null)
                        {
                            var skill = catalog.getItemFromGuid(character.selectedItem.item.expendableWithSkill.skill) as Common.Rom.NSkill;

                            if (skill != null)
                            {
                                switch (skill.option.target)
                                {
                                    case Rom.TargetType.PARTY_ONE:
                                    case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                                        if (IsNotActiveTarget(target) || target == null || !IsHitRange(character, skill.Range, target) || (skill.friendEffect != null
                                            && ((target.IsDeadCondition() != skill.friendEffect.HasDeadCondition(catalog)) && skill.IsOnlyForDown(catalog))))
                                        {
                                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is Not Available.");
                                            isRetarget = true;
                                        }
                                        break;

                                    case Rom.TargetType.ENEMY_ONE:
                                    case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                                    case Rom.TargetType.SELF_ENEMY_ONE:
                                        if (IsNotActiveTarget(target) || target.IsDeadCondition() || !IsHitRange(character, skill.Range, target))
                                        {
                                            GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is Not Available.");
                                            isRetarget = true;
                                        }
                                        break;
                                }
                            }
                        }
                        else if (character.selectedItem.item.expendable != null)
                        {
                            if (IsNotActiveTarget(target) || (target.IsDeadCondition() != character.selectedItem.item.expendable.HasRecoveryDeadCondition(catalog)))
                            {
                                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, character.Name, "Retarget / Cause : Target is Not Available.");
                                isRetarget = true;
                            }
                        }
                    }
                    break;
                case BattleCommandType.Position:
                    if (character.targetCharacter.Length != 1)
                    {
                        isRetarget = true;
                    }
                    else if (gameSettings.useBehindParty)
                    {
                        isRetarget = character.targetCharacter[0].IsBehindPartyCondition() || character.IsBehindPartyCondition();
                    }
                    break;
            }

            return isRetarget;
        }

        private bool IsNotActiveTarget(BattleCharacterBase target)
        {
            if (playerData.Contains(target as BattlePlayerData))
                return false;
            if (enemyData.Contains(target as BattleEnemyData))
                return false;
            
            // Pokemon-style: Reserve party members are also valid targets for skills and items
            if (stockPlayerData.Contains(target as BattlePlayerData))
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Targeting", 
                    string.Format("Reserve member {0} is a valid target", target.Name));
                return false;
            }
            
            return true;
        }

        private BattleCharacterBase[] ReTarget(BattleCharacterBase character, bool isFriendRecoveryDownStatus)
        {
            var targets = new List<BattleCharacterBase>();
            var friendPartyMember = character.FriendPartyRefMember.Where(player => player.IsDeadCondition() == isFriendRecoveryDownStatus);
            var targetPartyMember = character.EnemyPartyRefMember.Where(enemy => !enemy.IsDeadCondition());

            switch (character.selectedBattleCommandType)
            {
                case BattleCommandType.Attack:
                case BattleCommandType.Critical:
                case BattleCommandType.ForceCritical:
                case BattleCommandType.Miss:
                {
                    var attackConditionTarget = GetAttackConditionTargetCharacter(character);

                    if (attackConditionTarget != null)
                    {
                        targets.Add(attackConditionTarget);
                    }
                    else
                    {
                        var tempTargets = new List<BattleCharacterBase>(targetPartyMember.Where(enemy => IsHitRange(character, enemy)));

                        if (tempTargets.Count() > 0)
                        {
                            targets.Add(TargetSelectWithHateRate(tempTargets, character as BattleEnemyData));
                        }
                    }
                }
                break;

                // どちらも対象が自分自身なので再抽選の必要は無し
                // In both cases, the target is yourself, so there is no need to re-lottery.
                case BattleCommandType.Charge:
                case BattleCommandType.Guard:
                    targets.Add(character);
                    break;

                case BattleCommandType.SameSkillEffect:
                {
                    var skill = catalog.getItemFromGuid(character.selectedBattleCommand.refGuid) as Rom.NSkill;

                    switch (skill.option.target)
                    {
                        case Rom.TargetType.ENEMY_ONE:
                        case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                        case Rom.TargetType.SELF_ENEMY_ONE:
                        case Rom.TargetType.OTHERS_ENEMY_ONE:
                            if (targetPartyMember.Count() > 0) targets.Add(targetPartyMember.ElementAt(battleRandom.Next(targetPartyMember.Count())));
                            break;
                        case Rom.TargetType.PARTY_ONE:
                        case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                            if (friendPartyMember.Count() > 0) targets.Add(friendPartyMember.ElementAt(battleRandom.Next(friendPartyMember.Count())));
                            break;
                    }

                    targets.RemoveAll(x => !IsHitRange(character, skill.Range, x));
                }
                break;

                case BattleCommandType.Skill:
                    switch (character.selectedSkill.option.target)
                    {
                        case Rom.TargetType.ENEMY_ONE:
                        case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                        case Rom.TargetType.SELF_ENEMY_ONE:
                        case Rom.TargetType.OTHERS_ENEMY_ONE:
                            if (targetPartyMember.Count() > 0) targets.Add(TargetSelectWithHateRate(targetPartyMember, character as BattleEnemyData));
                            break;
                        case Rom.TargetType.PARTY_ONE:
                        case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                            if (friendPartyMember.Count() > 0) targets.Add(friendPartyMember.ElementAt(battleRandom.Next(friendPartyMember.Count())));
                            break;
                    }

                    targets.RemoveAll(x => !IsHitRange(character, character.selectedSkill.Range, x));
                    break;

                case BattleCommandType.Item:
                    if (character.selectedItem.item.IsExpandable)
                    {
                        if (character.selectedItem.item.expendable.HasRecoveryDeadCondition(catalog))
                        {
                            var a = character.FriendPartyRefMember.Where(player => player.HitPoint == 0
                                && ((BattlePlayerData)player).player.isAvailableItem(character.selectedItem.item));

                            if (a.Count() > 0) targets.Add(a.ElementAt(battleRandom.Next(a.Count())));
                        }
                        else
                        {
                            var a = character.FriendPartyRefMember.Where(player => player.HitPoint > 0
                                && ((BattlePlayerData)player).player.isAvailableItem(character.selectedItem.item));

                            if (a.Count() > 0) targets.Add(a.ElementAt(battleRandom.Next(a.Count())));
                        }

                        targets.RemoveAll(x => !IsHitRange(character, character.selectedItem.item.Range, x));
                    }
                    else if (character.selectedItem.item.IsExpandableWithSkill)
                    {
                        var skill = catalog.getItemFromGuid(character.selectedItem.item.expendableWithSkill.skill) as Common.Rom.NSkill;

                        switch (skill.option.target)
                        {
                            case Rom.TargetType.PARTY_ONE:
                            case Rom.TargetType.PARTY_ONE_ENEMY_ALL:
                                if (skill.friendEffect.HasDeadCondition(catalog))
                                {
                                    var a = character.FriendPartyRefMember.Where(player => (skill.option.onlyForDown || player.HitPoint == 0)
                                        && ((BattlePlayerData)player).player.isAvailableItem(character.selectedItem.item));

                                    if (a.Count() > 0) targets.Add(a.ElementAt(battleRandom.Next(a.Count())));
                                }
                                else
                                {
                                    var a = character.FriendPartyRefMember.Where(player => player.HitPoint > 0
                                        && ((BattlePlayerData)player).player.isAvailableItem(character.selectedItem.item));

                                    if (a.Count() > 0) targets.Add(a.ElementAt(battleRandom.Next(a.Count())));
                                }
                                break;
                            case Rom.TargetType.ENEMY_ONE:
                            case Rom.TargetType.PARTY_ALL_ENEMY_ONE:
                            case Rom.TargetType.SELF_ENEMY_ONE:
                            {
                                var a = character.EnemyPartyRefMember.Where(enemy => enemy.HitPoint > 0);

                                if (a.Count() > 0) targets.Add(a.ElementAt(battleRandom.Next(a.Count())));
                            }
                            break;
                        }

                        targets.RemoveAll(x => !IsHitRange(character, skill.Range, x));
                    }
                    break;
            }

            return targets.ToArray();
        }

        public override void RegisterTestEffect(string effectNameKey, Resource.NSprite effect, Catalog catalog)
        {
            //effectCatalog = catalog;
        }

        internal void SetBackGroundColor(Color color)
        {
            backGroundStyle = BackGroundStyle.FillColor;

            backGroundColor = color;
        }

        internal void SetBackGroundImage(string path)
        {
            SetBackGroundImage(Graphics.LoadImage(path));
        }
        internal void SetBackGroundImage(Common.Resource.Texture imageId)
        {
            backGroundStyle = BackGroundStyle.Image;

            backGroundImageId = imageId;
        }

        internal void SetBackGroundModel(Common.Resource.Model model)
        {
            /*backGroundModel = */
            new SharpKmyGfx.ModelInstance(model.m_mdl, null, System.Guid.Empty);
            backGroundStyle = BackGroundStyle.Model;
        }

        public override void Prepare()
        {
            if (!owner.IsBattle2D)
            {
                ((BattleViewer3D)battleViewer).prepare();
            }
        }
        public override void Prepare(Guid battleBg)
        {
            if (!owner.IsBattle2D)
            {
                ((BattleViewer3D)battleViewer).prepare(battleBg);
            }
        }


#if !WINDOWS
        // TODO:仮実装:Error回避の為
        // TODO: Temporary implementation: To avoid errors
        internal IEnumerator prepare_enum()
        {
            prepare();
            yield return null;
        }
        internal IEnumerator prepare_enum(Guid battleBg)
        {
            prepare(battleBg);
            yield return null;
        }
#endif




        public override bool IsWrongFromCurrentBg(Guid battleBg)
        {
            if (!owner.IsBattle2D)
            {
                return ((BattleViewer3D)battleViewer).mapDrawer.mapRom.guId != battleBg;
            }

            return false;
        }

        internal void UpdateCollisionDepotInUnity()
        {
            var battleViewer3D = battleViewer as BattleViewer3D;
            if (battleViewer3D == null)
            {
                return;
            }
            battleViewer3D.UpdateCollisionDepotInUnity();
        }

        internal void setActorsVisibility(bool flg)
        {
            var battleViewer3D = battleViewer as BattleViewer3D;
            if (battleViewer3D == null)
            {
                return;
            }
            battleViewer3D.setFriendsVisibility(flg);
        }

        //------------------------------------------------------------------------------
        /**
         *	スクリーンフェードカラーを取得
         */
        public override SharpKmyGfx.Color GetFadeScreenColor()
        {
            SharpKmyGfx.Color col = new SharpKmyGfx.Color(0, 0, 0, 0);

            switch (battleState)
            {
                case BattleState.StartFlash:
                case BattleState.StartFadeOut:
                case BattleState.FinishFadeIn:
                case BattleState.StartFadeIn:
                case BattleState.SetFinishEffect:
                case BattleState.FinishFadeOut:
                    col.r = (float)fadeScreenColorTweener.CurrentValue.R / 255.0f;
                    col.g = (float)fadeScreenColorTweener.CurrentValue.G / 255.0f;
                    col.b = (float)fadeScreenColorTweener.CurrentValue.B / 255.0f;
                    col.a = (float)fadeScreenColorTweener.CurrentValue.A / 255.0f;
                    break;
            }

            return col;
        }

        //------------------------------------------------------------------------------
        /**
         *	バトルステートを取得
         */
        public override BattleState GetBattleState()
        {
            return battleState;
        }


        public override BattleResultState GetBattleResult()
        {
            return BattleResult;
        }

        public override MapData GetMapDrawer()
        {
            return (battleViewer as BattleViewer3D)?.mapDrawer;
        }

        public override SharpKmyMath.Matrix4 GetCameraProjectionMatrix()
        {
            return (battleViewer as BattleViewer3D)?.p ?? SharpKmyMath.Matrix4.identity();
        }

        public override SharpKmyMath.Matrix4 GetCameraViewMatrix()
        {
            return (battleViewer as BattleViewer3D)?.v ?? SharpKmyMath.Matrix4.identity();
        }

        public override void ReloadUI(Rom.LayoutProperties.LayoutNode.UsageInGame usage)
        {
            (battleViewer as BattleViewer3D)?.ReloadUI(usage);
        }

        public override MapScene GetEventController()
        {
            return battleEvents;
        }

        // ================================
        // EVOLUTION/DIGIMON SYSTEM
        // ================================

        /// <summary>
        /// Prepare evolution by checking character's evolution tags and level requirements
        /// Uses $evo tags in character ROM data: $evo(CharacterName,RequiredLevel)
        /// </summary>
        /// <returns>True if evolution options are available</returns>
        private bool PrepareEvolution()
        {
            if (catalog.getItemFromGuid(activeCharacter.Hero.rom.guId) is Rom.Cast activeRom)
            {
                var unhandledList = Tools.GetTagMultipleValues(activeRom.tags, "$evo");
                var handledList = new List<string>();
                var lastTarget = (int)BattleEventController.lastSkillTargetIndex;

                foreach (var unhandledtag in unhandledList)
                {
                    int requiredLevel = 0;
                    var evoAndLevel = unhandledtag.Split(',');

                    if (string.IsNullOrEmpty(evoAndLevel[0])) continue;
                    if (evoAndLevel.Length == 2)
                        int.TryParse(evoAndLevel[1], out requiredLevel);

                    if (party.Players[lastTarget].level < requiredLevel) continue;

                    handledList.Add(evoAndLevel[0]);
                }

                currentEvolutionList = handledList.ToArray();
                if (currentEvolutionList.Length >= 1) return true;
            }

            return false;
        }

        /// <summary>
        /// Handle evolution choice selection UI
        /// </summary>
        private void ChoiceEvolution()
        {
            if (!battleEvents.IsVisibleChoices() && !showingShoices)
            {
                battleEvents.ShowChoices(currentEvolutionList, 4);
                showingShoices = true;
            }

            GameMain.PushLog(DebugDialog.LogEntry.LogType.EVENT, "DigiBattle", battleEvents.GetChoicesResult().ToString());
            var choice = battleEvents.GetChoicesResult();
            if (choice != -1)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.EVENT, "DigiBattle", "option selected");
                currentIndexSelected = choice;
                selectingEvoDone = true;

                // Direct evolution without spinning animation
                var currentTarget = (int)BattleEventControllerBase.lastSkillTargetIndex;
                var playerToEvolve = playerData[currentTarget];

                if (playerToEvolve.MagicPoint <= 1)
                {
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Evolution", "Not enough MP for evolution!");
                    
                    // Reset evolution state flags on failure
                    showingShoices = false;
                    selectingEvoDone = false;
                    currentIndexSelected = -1;
                    currentEvolutionList = null;
                    return;
                }

                // Capture original direction for restoration
                prevDirection = playerToEvolve.mapChr.getDirectionRadian();
                GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Evolution", 
                    string.Format("Captured original direction: {0} radians", prevDirection));

                // Execute evolution immediately (no spinning delay)
                ExecuteDigiEvolution();
            }
        }

        /// <summary>
        /// Execute evolution when system switch is triggered
        /// </summary>
        private void ExecuteDigiEvolution()
        {
            if (GameMain.instance.data.system.GetSwitch("executeDigievo"))
            {
                PerformDigiEvolution();
                showingShoices = false;
                selectingEvoDone = false;
                GameMain.instance.data.system.SetSwitch("executeDigievo", false, Guid.Empty, false);
            }
        }

        /// <summary>
        /// Perform the actual evolution transformation
        /// </summary>
        private void PerformDigiEvolution()
        {
            if (currentEvolutionList.Length <= currentIndexSelected) return;
            if (!(catalog.getItemFromName(currentEvolutionList[currentIndexSelected], typeof(Rom.Cast)) is Rom.Cast rom)) return;

            bool test()
            {
                var hero = Party.createHeroFromRom(catalog, rom);
                if (hero != null)
                {
                    var currentTarget = (int)BattleEventControllerBase.lastSkillTargetIndex;
                    var playerToEvolve = playerData[currentTarget];

                    if (playerToEvolve.MagicPoint <= 1)
                    {
                        return false;
                    }

                    var battleviewer3d = battleViewer as BattleViewer3D;
                    var positionPreEvo = battleviewer3d.friends[currentTarget].mapChr.pos;
                    var positionCache = new Vector3(positionPreEvo.X, positionPreEvo.Y, positionPreEvo.Z);

                    var isMovableBack = playerToEvolve.isMovableToForward(false);
                    var graphic = catalog.getItemFromGuid(hero.rom.Graphic, false) as Common.Resource.GfxResourceBase;
                    hero.SetLevel(party.Players[currentTarget].level, catalog, party);
                    playerData[currentTarget].MagicPoint /= 2;
                    var percentageToReduce = 100 - (playerData[currentTarget].MagicPointPercent * 100);
                    playerToEvolve.SetParameters(hero, owner.debugSettings.battleHpAndMpMax, owner.debugSettings.battleStatusMax, party);
                    playerToEvolve.player = hero;
                    playerToEvolve.mapChr.ChangeGraphic(graphic, battleEvents.mapDrawer);
                    playerToEvolve.MagicPoint -= (int)((percentageToReduce / 100) * playerToEvolve.MagicPoint);

                    // SIMPLE DIRECTION RESTORATION
                    // Capture direction in local variable (prevDirection gets reset to -1 immediately)
                    var savedDirection = prevDirection;
                    
                    // Restore direction immediately after ChangeGraphic
                    if (savedDirection != -1)
                    {
                        playerToEvolve.mapChr.setDirectionFromRadian(savedDirection, true, true);
                        playerToEvolve.directionRad = savedDirection;
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Evolution", 
                            string.Format("Direction restored: {0} radians", savedDirection));
                    }
                    else
                    {
                        // Fallback: face UP if no original direction stored
                        playerToEvolve.mapChr.setDirectionFromRadian(-(float)Math.PI / 2, true, true);
                        playerToEvolve.directionRad = -(float)Math.PI / 2;
                        GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Evolution", 
                            "No original direction found, defaulting to UP/north");
                    }
                    prevDirection = -1;

                    battleviewer3d.friends[currentTarget].mapChr.pos = positionCache;
                    SetNextBattleStatus(playerToEvolve);

                    // Play attack motion briefly, then return to wait
                    playerToEvolve.mapChr.playMotion("attack", 0.2f, false, true);
                    playerToEvolve.mapChr.playMotion("wait", 0.2f, false, false);
                    
                    // Log the evolution for debugging
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Evolution", 
                        string.Format("Player {0} evolved to {1}! Level: {2}, MP cost: 50%", 
                        currentTarget, rom.name, hero.level));
                        
                    // CRITICAL: Reset evolution state flags to prevent infinite loop
                    showingShoices = false;
                    selectingEvoDone = false;
                    currentIndexSelected = -1;
                    currentEvolutionList = null;
                    
                    GameMain.PushLog(DebugDialog.LogEntry.LogType.BATTLE, "Evolution", "Evolution completed, state reset");
                }
                return true; // IMPORTANT: Return true to complete evolution
            }

            GameMain.instance.pushTask(test);
        }
        
        /// <summary>
        /// EV SYSTEM: Process EV gains from defeated enemy
        /// Reads $EVgiven() tag and applies EVs to all participating party members
        /// </summary>
        /// <param name="defeatedEnemy">The enemy that was just defeated</param>
        private void ProcessEVGains(BattleEnemyData defeatedEnemy)
        {
            try
            {
                // Get the enemy's ROM data
                var enemyRom = defeatedEnemy.monster;
                if (enemyRom == null)
                {
                    Tools.PushLog("EV Debug: Enemy ROM is null!");
                    return;
                }
                
                // DEBUG: Log enemy ROM info
                Tools.PushLog($"EV Debug: Processing enemy '{enemyRom.name}', tags: '{enemyRom.tags}'");
                
                // Parse $EVgiven() tag from enemy ROM (using same method as Evolution system)
                // NOTE: GetTagMultipleValues converts to lowercase, so search parameter must be lowercase
                var evTagList = Tools.GetTagMultipleValues(enemyRom.tags, "$evgiven");
                if (evTagList.Count == 0)
                {
                    // No EV tag found - this enemy gives no EVs
                    Tools.PushLog($"EV Debug: No $evgiven tag found for enemy '{enemyRom.name}'");
                    return;
                }
                
                // Take the first EV tag found (should only be one)
                var evTag = evTagList[0];
                
                Tools.PushLog($"EV Processing: Enemy {enemyRom.name} gives EVs: {evTag}");
                
                // Parse EV values (format: "atk:2,def:1,spd:1")
                var evGains = ParseEVValues(evTag);
                if (evGains.Count == 0)
                {
                    Tools.PushLog("EV Processing: No valid EV values found in tag");
                    return;
                }
                
                // Apply EVs to all participating party members
                ApplyEVsToParty(evGains, enemyRom.name);
            }
            catch (Exception ex)
            {
                Tools.PushLog($"EV Processing Error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Parse EV values from tag string (e.g., "atk:2,def:1,spd:1")
        /// </summary>
        /// <param name="evTag">The EV tag content</param>
        /// <returns>Dictionary of stat names to EV gains</returns>
        private Dictionary<string, int> ParseEVValues(string evTag)
        {
            var evGains = new Dictionary<string, int>();
            
            if (string.IsNullOrEmpty(evTag)) return evGains;
            
            // Split by comma and parse each stat:value pair
            var pairs = evTag.Split(',');
            foreach (var pair in pairs)
            {
                var parts = pair.Trim().Split(':');
                if (parts.Length == 2)
                {
                    var statName = parts[0].Trim().ToLower();
                    if (int.TryParse(parts[1].Trim(), out int evValue) && evValue > 0)
                    {
                        evGains[statName] = evValue;
                    }
                }
            }
            
            return evGains;
        }
        
        /// <summary>
        /// Apply EV gains to party members who participated in battle
        /// </summary>
        /// <param name="evGains">Dictionary of stat names to EV gains</param>
        /// <param name="enemyName">Name of defeated enemy for logging</param>
        private void ApplyEVsToParty(Dictionary<string, int> evGains, string enemyName)
        {
            // Apply EVs ONLY to active party members who participated in battle
            // Reserve members do NOT gain EVs (must participate to earn them)
            foreach (var player in playerData)
            {
                if (player.HitPoint > 0 || player.IsDeadCondition()) // Include KO'd players who participated
                {
                    ApplyEVsToHero(player.player, evGains, enemyName);
                }
            }
            
            // Log participation count for debugging
            var participantCount = playerData.Count(p => p.HitPoint > 0 || p.IsDeadCondition());
            Tools.PushLog($"EV Distribution: {participantCount} party members participated and gained EVs");
        }
        
        /// <summary>
        /// Apply EV gains to a specific Hero
        /// </summary>
        /// <param name="hero">The hero to gain EVs</param>
        /// <param name="evGains">Dictionary of stat names to EV gains</param>
        /// <param name="enemyName">Name of defeated enemy for logging</param>
        private void ApplyEVsToHero(Hero hero, Dictionary<string, int> evGains, string enemyName)
        {
            if (hero?.statusValue == null) return;
            
            var totalEVsGained = 0;
            var evReport = new List<string>();
            
            foreach (var evPair in evGains)
            {
                var statName = evPair.Key;
                var evGain = evPair.Value;
                
                // Get the corresponding status GUID for this stat
                var statusGuid = GetStatusGuidForStat(statName);
                if (statusGuid == Guid.Empty)
                {
                    Tools.PushLog($"EV Warning: Unknown stat '{statName}' - skipping");
                    continue;
                }
                
                // Get current stat value
                var currentValue = hero.statusValue.GetStatus(statusGuid);
                var newValue = currentValue + evGain;
                
                // Apply the EV gain to the hero's permanent stats
                hero.statusValue.SetStatus(statusGuid, newValue);
                
                totalEVsGained += evGain;
                evReport.Add($"{statName.ToUpper()}+{evGain}");
                
                Tools.PushLog($"EV Gain: {hero.rom.name} gained {evGain} {statName.ToUpper()} EV (was {currentValue}, now {newValue})");
            }
            
            if (totalEVsGained > 0)
            {
                var evSummary = string.Join(", ", evReport);
                Tools.PushLog($"EV Summary: {hero.rom.name} gained {evSummary} from defeating {enemyName}");
            }
        }
        
        /// <summary>
        /// Get the Status GUID for a stat name
        /// Maps stat names to Bakin's status system GUIDs
        /// </summary>
        /// <param name="statName">The stat name (mhp, atk, def, etc.)</param>
        /// <returns>The corresponding status GUID, or Guid.Empty if not found</returns>
        private Guid GetStatusGuidForStat(string statName)
        {
            try
            {
                var gameSettings = catalog.getGameSettings();
                
                switch (statName.ToLower())
                {
                    case "mhp":
                    case "hp":
                        return gameSettings.maxHPStatusID;
                        
                    case "mmp":
                    case "mp":
                        return gameSettings.maxMPStatusID;
                        
                    case "atk":
                    case "attack":
                        // Search through CastStatusParamInfoList for attack-related status
                        return FindStatusGuidByIndex(gameSettings, 2); // Index 2 is typically attack
                        
                    case "def":
                    case "defense": 
                        // Search through CastStatusParamInfoList for defense-related status
                        return FindStatusGuidByIndex(gameSettings, 3); // Index 3 is typically defense
                        
                    case "mgc":
                    case "magic":
                        // Search through CastStatusParamInfoList for magic power-related status
                        return FindStatusGuidByIndex(gameSettings, 4); // Index 4 is typically magic power
                        
                    case "spd":
                    case "speed":
                    case "agility":
                        // Search through CastStatusParamInfoList for agility-related status
                        return FindStatusGuidByIndex(gameSettings, 5); // Index 5 is typically agility
                        
                    case "dex":
                    case "accuracy":
                        // Search through CastStatusParamInfoList for accuracy-related status
                        return FindStatusGuidByIndex(gameSettings, 6); // Index 6 is typically accuracy
                        
                    case "rcv":
                    case "evasion":
                        // Search through CastStatusParamInfoList for evasion-related status
                        return FindStatusGuidByIndex(gameSettings, 7); // Index 7 is typically evasion
                        
                    default:
                        Tools.PushLog($"EV Error: Unknown stat name '{statName}'");
                        return Guid.Empty;
                }
            }
            catch (Exception ex)
            {
                Tools.PushLog($"EV Error getting status GUID for '{statName}': {ex.Message}");
                return Guid.Empty;
            }
        }
        
        /// <summary>
        /// Find status GUID by index in the CastStatusParamInfoList
        /// </summary>
        /// <param name="gameSettings">Game settings instance</param>
        /// <param name="index">Status parameter index</param>
        /// <returns>The status GUID, or Guid.Empty if not found</returns>
        private Guid FindStatusGuidByIndex(Rom.GameSettings gameSettings, int index)
        {
            try
            {
                var info = gameSettings.GetCastStatusParamInfo(index);
                if (info != null)
                {
                    Tools.PushLog($"EV Debug: Found status GUID for index {index}: {info.guId}");
                    return info.guId;
                }
            }
            catch (Exception ex)
            {
                Tools.PushLog($"EV Warning: Could not find status for index {index}: {ex.Message}");
            }
            
            return Guid.Empty;
        }
        
        #region IV System
        
        /// <summary>
        /// IV SYSTEM: Generate random Individual Values for captured hero
        /// IVs are permanent "genetic" stats that make each captured monster unique
        /// IMPORTANT: This must be called AFTER the hero is added to the party to prevent stat reset
        /// </summary>
        /// <param name="capturedHero">The hero that was just captured and added to party</param>
        /// <param name="uniqueName">Unique name for logging</param>
        private void GenerateIVsForCapturedHero(Hero capturedHero, string uniqueName)
        {
            try
            {
                if (capturedHero?.statusValue == null)
                {
                    Tools.PushLog("IV Debug: Captured hero or statusValue is null!");
                    return;
                }
                
                Tools.PushLog($"IV Debug: Generating IVs for {uniqueName}...");
                
                var gameSettings = catalog.getGameSettings();
                var ivReport = new List<string>();
                var totalIVs = 0;
                
                // Generate random IVs (0-31) for each stat using Bakin's battle random
                var iv_mhp = battleRandom.Next(32);   // Max HP IV (0-31)
                var iv_mmp = battleRandom.Next(32);   // Max MP IV (0-31)  
                var iv_atk = battleRandom.Next(32);   // Attack IV (0-31)
                var iv_def = battleRandom.Next(32);   // Defense IV (0-31)
                var iv_mgc = battleRandom.Next(32);   // Magic IV (0-31)
                var iv_spd = battleRandom.Next(32);   // Agility IV (0-31)
                var iv_dex = battleRandom.Next(32);   // Accuracy IV (0-31)
                var iv_rcv = battleRandom.Next(32);   // Evasion IV (0-31)
                
                // Apply IVs to hero's permanent stats
                ApplyIVToStat(capturedHero, gameSettings.maxHPStatusID, iv_mhp, "HP", ivReport, ref totalIVs);
                ApplyIVToStat(capturedHero, gameSettings.maxMPStatusID, iv_mmp, "MP", ivReport, ref totalIVs);
                ApplyIVToStat(capturedHero, FindStatusGuidByIndex(gameSettings, 2), iv_atk, "ATK", ivReport, ref totalIVs);
                ApplyIVToStat(capturedHero, FindStatusGuidByIndex(gameSettings, 3), iv_def, "DEF", ivReport, ref totalIVs);
                ApplyIVToStat(capturedHero, FindStatusGuidByIndex(gameSettings, 4), iv_mgc, "MGC", ivReport, ref totalIVs);
                ApplyIVToStat(capturedHero, FindStatusGuidByIndex(gameSettings, 5), iv_spd, "SPD", ivReport, ref totalIVs);
                ApplyIVToStat(capturedHero, FindStatusGuidByIndex(gameSettings, 6), iv_dex, "DEX", ivReport, ref totalIVs);
                ApplyIVToStat(capturedHero, FindStatusGuidByIndex(gameSettings, 7), iv_rcv, "RCV", ivReport, ref totalIVs);
                
                // Log IV summary
                var ivSummary = string.Join(", ", ivReport);
                Tools.PushLog($"IV Generation: {uniqueName} gained IVs: {ivSummary} (Total: {totalIVs}/248)");
                
                // Calculate IV percentage (perfect is 248 total, 31*8)
                var ivPercentage = (totalIVs / 248.0f) * 100;
                Tools.PushLog($"IV Quality: {uniqueName} has {ivPercentage:F1}% IV rating");
            }
            catch (Exception ex)
            {
                Tools.PushLog($"IV Generation Error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Apply a single IV to a hero stat
        /// </summary>
        /// <param name="hero">Hero to modify</param>
        /// <param name="statusGuid">Status GUID for the stat</param>
        /// <param name="ivValue">IV value (0-31)</param>
        /// <param name="statName">Stat name for logging</param>
        /// <param name="ivReport">List to add reporting info</param>
        /// <param name="totalIVs">Running total of IVs</param>
        private void ApplyIVToStat(Hero hero, Guid statusGuid, int ivValue, string statName, List<string> ivReport, ref int totalIVs)
        {
            if (statusGuid == Guid.Empty)
            {
                Tools.PushLog($"IV Warning: Could not find status GUID for {statName} - skipping");
                return;
            }
            
            // Get current stat value and add IV bonus
            var currentValue = hero.statusValue.GetStatus(statusGuid);
            var newValue = currentValue + ivValue;
            
            // Apply the IV boost to permanent stats (statusValue)
            hero.statusValue.SetStatus(statusGuid, newValue);
            
            // CRITICAL: Also apply to Hero's direct stat properties for UI display
            // The character sheet reads from Hero properties, not just statusValue
            switch (statName.ToUpper())
            {
                case "HP":
                    hero.maxHitpoint += ivValue;
                    Tools.PushLog($"IV Direct: Applied {ivValue} to hero.maxHitpoint (now {hero.maxHitpoint})");
                    break;
                case "MP":
                    hero.maxMagicpoint += ivValue;
                    Tools.PushLog($"IV Direct: Applied {ivValue} to hero.maxMagicpoint (now {hero.maxMagicpoint})");
                    break;
                case "ATK":
                    hero.power += ivValue;
                    Tools.PushLog($"IV Direct: Applied {ivValue} to hero.power (now {hero.power})");
                    break;
                case "DEF":
                    hero.vitality += ivValue; // Defense maps to vitality in Bakin
                    Tools.PushLog($"IV Direct: Applied {ivValue} to hero.vitality (now {hero.vitality})");
                    break;
                case "MGC":
                    hero.magic += ivValue;
                    Tools.PushLog($"IV Direct: Applied {ivValue} to hero.magic (now {hero.magic})");
                    break;
                case "SPD":
                    hero.speed += ivValue;
                    Tools.PushLog($"IV Direct: Applied {ivValue} to hero.speed (now {hero.speed})");
                    break;
                case "DEX":
                    hero.dexterity += ivValue;
                    Tools.PushLog($"IV Direct: Applied {ivValue} to hero.dexterity (now {hero.dexterity})");
                    break;
                case "RCV":
                    hero.recovery += ivValue;
                    Tools.PushLog($"IV Direct: Applied {ivValue} to hero.recovery (now {hero.recovery})");
                    break;
            }
            
            // Add to reporting
            ivReport.Add($"{statName}+{ivValue}");
            totalIVs += ivValue;
            
            Tools.PushLog($"IV Applied: {statName} IV+{ivValue} (was {currentValue}, now {newValue})");
        }
        
        #endregion
        
        #region Nature System
        
        /// <summary>
        /// Nature definitions - each nature modifies certain stats
        /// Format: NatureID -> (BoostedStat, ReducedStat, NatureName)
        /// </summary>
        private static readonly Dictionary<int, (string boosted, string reduced, string name)> NatureDefinitions = new Dictionary<int, (string, string, string)>
        {
            { 0, ("ATK", "DEF", "Hardy") },      // +Attack, -Defense  
            { 1, ("ATK", "SPD", "Brave") },      // +Attack, -Speed
            { 2, ("ATK", "MGC", "Adamant") },    // +Attack, -Magic
            { 3, ("ATK", "RCV", "Naughty") },    // +Attack, -Recovery
            { 4, ("DEF", "ATK", "Bold") },       // +Defense, -Attack
            { 5, ("DEF", "SPD", "Impish") },     // +Defense, -Speed  
            { 6, ("DEF", "MGC", "Lax") },        // +Defense, -Magic
            { 7, ("DEF", "RCV", "Relaxed") },    // +Defense, -Recovery
            { 8, ("SPD", "ATK", "Timid") },      // +Speed, -Attack
            { 9, ("SPD", "DEF", "Hasty") },      // +Speed, -Defense
            { 10, ("SPD", "MGC", "Jolly") },     // +Speed, -Magic
            { 11, ("SPD", "RCV", "Naive") },     // +Speed, -Recovery
            { 12, ("MGC", "ATK", "Modest") },    // +Magic, -Attack
            { 13, ("MGC", "DEF", "Mild") },      // +Magic, -Defense
            { 14, ("MGC", "SPD", "Quiet") },     // +Magic, -Speed
            { 15, ("MGC", "RCV", "Rash") },      // +Magic, -Recovery
            { 16, ("RCV", "ATK", "Calm") },      // +Recovery, -Attack
            { 17, ("RCV", "DEF", "Gentle") },    // +Recovery, -Defense
            { 18, ("RCV", "SPD", "Careful") },   // +Recovery, -Speed
            { 19, ("RCV", "MGC", "Sassy") },     // +Recovery, -Magic
            { 20, ("", "", "Serious") },         // Neutral nature (no stat changes)
            { 21, ("", "", "Docile") },          // Neutral nature (no stat changes)
            { 22, ("", "", "Bashful") },         // Neutral nature (no stat changes)
            { 23, ("", "", "Quirky") },          // Neutral nature (no stat changes)
            { 24, ("", "", "Hardy") }            // Neutral nature (no stat changes)
        };
        
        /// <summary>
        /// Generates and applies a random nature to a captured hero
        /// </summary>
        /// <param name="capturedHero">The captured hero to apply nature to</param>
        /// <param name="uniqueName">Unique name for logging</param>
        private void GenerateNatureForCapturedHero(Hero capturedHero, string uniqueName)
        {
            try
            {
                Tools.PushLog($"Nature Debug: Generating nature for {uniqueName}...");
                
                // Generate random nature ID (0-24)
                int natureId = battleRandom.Next(25);
                
                // Get nature definition
                var nature = NatureDefinitions[natureId];
                Tools.PushLog($"Nature Selected: {uniqueName} got {nature.name} nature (ID: {natureId})");
                
                // Store nature ID in custom Nature status
                var natureStatusGuid = GetNatureStatusGuid();
                if (natureStatusGuid != Guid.Empty)
                {
                    capturedHero.statusValue.SetStatus(natureStatusGuid, natureId);
                    Tools.PushLog($"Nature Stored: {uniqueName} nature ID {natureId} saved to statusValue");
                }
                else
                {
                    Tools.PushLog($"Nature Error: Could not find Nature status GUID for {uniqueName}");
                    return;
                }
                
                // Apply nature stat modifications (10% boost/reduction)
                if (!string.IsNullOrEmpty(nature.boosted) && !string.IsNullOrEmpty(nature.reduced))
                {
                    ApplyNatureModifications(capturedHero, nature.boosted, nature.reduced, uniqueName, nature.name);
                }
                else
                {
                    Tools.PushLog($"Nature Effect: {uniqueName} has neutral nature {nature.name} - no stat changes");
                }
            }
            catch (Exception ex)
            {
                Tools.PushLog($"Nature Generation Error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Apply nature stat modifications to a hero
        /// </summary>
        /// <param name="hero">Hero to modify</param>
        /// <param name="boostedStat">Stat to boost by 10%</param>
        /// <param name="reducedStat">Stat to reduce by 10%</param>
        /// <param name="uniqueName">Name for logging</param>
        /// <param name="natureName">Nature name for logging</param>
        private void ApplyNatureModifications(Hero hero, string boostedStat, string reducedStat, string uniqueName, string natureName)
        {
            // Apply boost (+10%)
            ApplyNatureStatChange(hero, boostedStat, 1.1f, uniqueName, natureName, "boosted");
            
            // Apply reduction (-10%)  
            ApplyNatureStatChange(hero, reducedStat, 0.9f, uniqueName, natureName, "reduced");
        }
        
        /// <summary>
        /// Apply nature modification to a specific stat
        /// </summary>
        /// <param name="hero">Hero to modify</param>
        /// <param name="statName">Stat to modify</param>
        /// <param name="multiplier">Multiplier (1.1 for boost, 0.9 for reduction)</param>
        /// <param name="uniqueName">Name for logging</param>
        /// <param name="natureName">Nature name for logging</param>
        /// <param name="changeType">Type of change for logging</param>
        private void ApplyNatureStatChange(Hero hero, string statName, float multiplier, string uniqueName, string natureName, string changeType)
        {
            switch (statName.ToUpper())
            {
                case "ATK":
                    {
                        int originalValue = hero.power;
                        int newValue = (int)Math.Round(originalValue * multiplier);
                        int change = newValue - originalValue;
                        hero.power = newValue;
                        
                        Tools.PushLog($"Nature Effect: {uniqueName} ({natureName}) ATK {changeType} by {change} (was {originalValue}, now {newValue})");
                    }
                    break;
                    
                case "DEF":
                    {
                        int originalValue = hero.vitality;
                        int newValue = (int)Math.Round(originalValue * multiplier);
                        int change = newValue - originalValue;
                        hero.vitality = newValue;
                        
                        Tools.PushLog($"Nature Effect: {uniqueName} ({natureName}) DEF {changeType} by {change} (was {originalValue}, now {newValue})");
                    }
                    break;
                    
                case "SPD":
                    {
                        int originalValue = hero.speed;
                        int newValue = (int)Math.Round(originalValue * multiplier);
                        int change = newValue - originalValue;
                        hero.speed = newValue;
                        
                        Tools.PushLog($"Nature Effect: {uniqueName} ({natureName}) SPD {changeType} by {change} (was {originalValue}, now {newValue})");
                    }
                    break;
                    
                case "MGC":
                    {
                        int originalValue = hero.magic;
                        int newValue = (int)Math.Round(originalValue * multiplier);
                        int change = newValue - originalValue;
                        hero.magic = newValue;
                        
                        Tools.PushLog($"Nature Effect: {uniqueName} ({natureName}) MGC {changeType} by {change} (was {originalValue}, now {newValue})");
                    }
                    break;
                    
                case "RCV":
                    {
                        int originalValue = hero.recovery;
                        int newValue = (int)Math.Round(originalValue * multiplier);
                        int change = newValue - originalValue;
                        hero.recovery = newValue;
                        
                        Tools.PushLog($"Nature Effect: {uniqueName} ({natureName}) RCV {changeType} by {change} (was {originalValue}, now {newValue})");
                    }
                    break;
            }
        }
        
        /// <summary>
        /// Get the GUID for the custom Nature status
        /// </summary>
        /// <returns>Nature status GUID or Guid.Empty if not found</returns>
        private Guid GetNatureStatusGuid()
        {
            try
            {
                var gameSettings = catalog.getGameSettings();
                
                // Search through all statuses to find one named "Nature"
                int statusIndex = 0;
                foreach (var info in gameSettings.CastStatusParamInfoList)
                {
                    if (info.name.Equals("Nature", StringComparison.OrdinalIgnoreCase))
                    {
                        Tools.PushLog($"Nature Status Found: Index {statusIndex}, GUID {info.guId}, Name: '{info.name}'");
                        return info.guId;
                    }
                    statusIndex++;
                }
                
                Tools.PushLog("Nature Status Error: 'Nature' status not found in statusList");
                return Guid.Empty;
            }
            catch (Exception ex)
            {
                Tools.PushLog($"Nature Status Error: {ex.Message}");
                return Guid.Empty;
            }
        }
        
        #endregion
        
        #region Shiny System
        
        /// <summary>
        /// Generates random shiny status for captured hero
        /// Shiny Pokemon are rare variants with 1/512 chance (configurable)
        /// </summary>
        /// <param name="capturedHero">The captured hero to check for shiny status</param>
        /// <param name="uniqueName">Unique name for logging</param>
        private void GenerateShinyStatusForCapturedHero(Hero capturedHero, string uniqueName)
        {
            try
            {
                Tools.PushLog($"Shiny Debug: Checking shiny status for {uniqueName}...");
                
                // Shiny rate: 1/512 (approximately 0.2% chance)
                // Change this value to adjust rarity: 4096 = 1/4096 (like real Pokemon), 100 = 1/100 (for testing)
                int shinyOdds = 512;
                int shinyRoll = battleRandom.Next(shinyOdds);
                bool isShiny = (shinyRoll == 0); // Only when roll equals 0
                
                // Get shiny status GUID
                var shinyStatusGuid = GetShinyStatusGuid();
                if (shinyStatusGuid != Guid.Empty)
                {
                    // Store shiny status: 0 = normal, 1 = shiny
                    int shinyValue = isShiny ? 1 : 0;
                    capturedHero.statusValue.SetStatus(shinyStatusGuid, shinyValue);
                    
                    if (isShiny)
                    {
                        Tools.PushLog($"✨ SHINY FOUND! ✨ {uniqueName} is a rare SHINY variant! (Roll: {shinyRoll}/{shinyOdds})");
                        Tools.PushLog($"Shiny Stored: {uniqueName} shiny status = 1 saved to statusValue");
                        
                        // Optional: Apply shiny bonus (small stat boost)
                        ApplyShinyStatBonus(capturedHero, uniqueName);
                    }
                    else
                    {
                        Tools.PushLog($"Shiny Check: {uniqueName} is normal (Roll: {shinyRoll}/{shinyOdds})");
                        Tools.PushLog($"Shiny Stored: {uniqueName} shiny status = 0 saved to statusValue");
                    }
                }
                else
                {
                    Tools.PushLog($"Shiny Error: Could not find Shiny status GUID for {uniqueName}");
                }
            }
            catch (Exception ex)
            {
                Tools.PushLog($"Shiny Generation Error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Apply small stat bonus to shiny Pokemon (optional enhancement)
        /// </summary>
        /// <param name="hero">Hero to enhance</param>
        /// <param name="uniqueName">Name for logging</param>
        private void ApplyShinyStatBonus(Hero hero, string uniqueName)
        {
            try
            {
                // Shiny bonus: +2 to all stats (small but noticeable)
                int shinyBonus = 2;
                
                hero.maxHitpoint += shinyBonus;
                hero.maxMagicpoint += shinyBonus;
                hero.power += shinyBonus;
                hero.vitality += shinyBonus;
                hero.magic += shinyBonus;
                hero.speed += shinyBonus;
                hero.dexterity += shinyBonus;
                hero.recovery += shinyBonus;
                
                Tools.PushLog($"Shiny Bonus: {uniqueName} gained +{shinyBonus} to all stats for being shiny!");
            }
            catch (Exception ex)
            {
                Tools.PushLog($"Shiny Bonus Error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get the GUID for the custom Shiny status
        /// </summary>
        /// <returns>Shiny status GUID or Guid.Empty if not found</returns>
        private Guid GetShinyStatusGuid()
        {
            try
            {
                var gameSettings = catalog.getGameSettings();
                
                // Search through all statuses to find one named "Shiny"
                int statusIndex = 0;
                foreach (var info in gameSettings.CastStatusParamInfoList)
                {
                    if (info.name.Equals("Shiny", StringComparison.OrdinalIgnoreCase))
                    {
                        Tools.PushLog($"Shiny Status Found: Index {statusIndex}, GUID {info.guId}, Name: '{info.name}'");
                        return info.guId;
                    }
                    statusIndex++;
                }
                
                Tools.PushLog("Shiny Status Error: 'Shiny' status not found in statusList");
                return Guid.Empty;
            }
            catch (Exception ex)
            {
                Tools.PushLog($"Shiny Status Error: {ex.Message}");
                return Guid.Empty;
            }
        }
        
        #endregion
    }

    /// <summary>
    /// Helper utilities for the evolution system
    /// Provides tag parsing and logging functionality
    /// </summary>
    internal static class Tools
    {
        /// <summary>
        /// Extract content between specified delimiters from a string
        /// </summary>
        /// <param name="str">Source string</param>
        /// <param name="from">Start delimiter (default: "(")</param>
        /// <param name="to">End delimiter (default: ")")</param>
        /// <returns>Content between delimiters, or null if not found</returns>
        public static string StringFromTo(string str, string from = "(", string to = ")")
        {
            if (string.IsNullOrEmpty(str) || !str.Contains(from) || !str.Contains(to)) return null;
            int fromInt = str.IndexOf(from) + from.Length;
            int toInt = str.LastIndexOf(to);
            return str.Substring(fromInt, toInt - fromInt);
        }

        /// <summary>
        /// Log message for evolution system debugging
        /// </summary>
        /// <param name="msg">Message to log</param>
        public static void PushLog(string msg)
        {
            GameMain.PushLog(DebugDialog.LogEntry.LogType.EVENT, "SpecialSkills", msg);
        }

        /// <summary>
        /// Get single tag value from character tags
        /// </summary>
        /// <param name="tags">Character tag string</param>
        /// <param name="targetTag">Tag to search for</param>
        /// <returns>Tag value content or null</returns>
        public static string GetTagValue(string tags, string targetTag)
        {
            var type = StringFromTo(tags.Split(new string[] { Environment.NewLine }, StringSplitOptions.None).
            FirstOrDefault(x => x.ToLower().Contains(targetTag)));
            return type;
        }

        /// <summary>
        /// Get multiple tag values from character tags
        /// Used for evolution trees: $evo(CharacterName,RequiredLevel)
        /// </summary>
        /// <param name="tags">Character tag string</param>
        /// <param name="targetTag">Tag to search for (e.g., "$evo")</param>
        /// <returns>List of tag values</returns>
        public static List<string> GetTagMultipleValues(string tags, string targetTag)
        {
            List<string> strings = new List<string>();
            var type = tags.Split(new string[] { Environment.NewLine }, StringSplitOptions.None).Where(x => x.ToLower().Contains(targetTag));
            foreach (var types in type)
            {
                var handledValue = Tools.StringFromTo(types);
                strings.Add(handledValue);
            }
            return strings;
        }
    }
}

