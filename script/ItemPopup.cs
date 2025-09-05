using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Yukar.Common;
using Yukar.Common.GameData;
using Yukar.Common.Resource;
using Yukar.Common.Rom;
using Yukar.Engine;
using Color = Microsoft.Xna.Framework.Color;
using Graphics = Yukar.Engine.Graphics;
using Texture = Yukar.Common.Resource.Texture;

// @@include PopupSettings.cs
// @@include ItemDrawer.cs

namespace Bakin
{
    internal class ItemPopup : BakinObject
    {
        private bool _useAddMinus = true;
        private bool _useItemIcon = true;
        private bool _useSound = true;
        private bool _usePopupColor;
        private bool _useBackground = true;
        private string _symbol = "x";
        private float _time;


        private readonly float _frequency = 0.1f;
        private int _maxItemsToShow = 4;

        // Text Colors
        private Color _removingColor = Color.IndianRed;
        private Color _addingColor = Color.LightGreen;
        private Color _textColor = Color.White;


        private Vector2 _textPosition = new Vector2(1044, 502);
        private Vector2 _iconPosition = new Vector2(1010, 500);
        private Vector2 _backgroundPosition = new Vector2(997, 500);
        private Vector2 _backgroundSize = new Vector2(300, 32);
        private Vector2 _iconSize = new Vector2(32, 32);
        private int _popupOffset = 45;

        private ItemDrawer _editorItem;
        private Texture _backgroundContainer;
        private bool _isEditMode;
        private bool _playerLocked;
        private int _target;
        private float _moveAmount = 8f;
        private string _currentMode = "Position";
        private string _targetName = "Item Name";
        private float _fontSize = 0.895f;
        private SharpKmyGfx.Texture _backgroundImage;

        private int _mode;
        private string _customBackground = "default";
        private string _customSound = "default";
        private SoundResource _sound;
        private int _soundId;

        private List<Party.ItemStack> _inventory = new List<Party.ItemStack>();
        private List<Party.ItemStack> _addedItems;
        private List<Party.ItemStack> _removedItems;
        private List<Party.ItemStack> _previousInventory = new List<Party.ItemStack>();
        private List<ItemDrawer> _itemsToDraw = new List<ItemDrawer>();
        private List<ItemDrawer> _itemsToRemove = new List<ItemDrawer>();

        private Guid guid = new Guid("88d733cf-ab1f-4f4b-a5d2-bb9c2f473e88"); // Don't change this
        private PopupSettings settings = new PopupSettings();
        private bool _usingChunk;
        private bool _disablePlugin;
        private List<Guid> _toPauseList = new List<Guid>();
        private List<Guid> layoutCacheList = new List<Guid>();
        private bool _showingChoices;
        ScriptRunner _textBoxRunner;
        private bool _showingTextBox;
        private float editorTimer;
        private bool _showingFunctionChoices;
        private int lastChoice;
        private readonly string NotPopupTag = "$notpopup";
        private readonly string DisablePluginSwitch = "disableItemPopup";
        public override void Start()
        {
            GameMain.PushLog(DebugDialog.LogEntry.LogType.EVENT, "ItemPopup", "Starting plugin");
            LoadChunkData();
            EditorItem();
            SetBackgroundImg();
            SetSound();
            SetInputText();

            // _previousInventory.AddRange(_inventory);
            // キャラクターが生成される時に、このメソッドがコールされます。
            // This method is called when the character is created.
        }

        private void EditorItem()
        {
            _inventory = mapScene.owner.data.party.Items;
            _previousInventory = mapScene.owner.data.party.Items;
            var editorItem = catalog.getFilteredItemList<NItem>().First() as NItem;

            if (editorItem == null)
            {
                editorItem = new NItem();
                editorItem.name = "Editor Item";
                catalog.addItem(editorItem, Catalog.OVERWRITE_RULES.ALWAYS_BUT_DONT_CHANGE_ORDER);
            }
            _editorItem = new ItemDrawer(new Party.ItemStack { item = editorItem, num = 1 }, _symbol, _textColor);
        }

        private void SetInputText()
        {
            Script inputText = new Script();
            inputText.commands.Add(new Script.Command() { type = Script.Command.FuncType.CHANGE_STRING_VARIABLE });

            inputText.commands[0].attrList.Add(new Script.StringAttr() { value = "popupTxtTemp" });
            inputText.commands[0].attrList.Add(new Script.IntAttr() { value = 30 });
            inputText.commands[0].attrList.Add(new Script.IntAttr() { value = 1 });
            inputText.commands[0].attrList.Add(new Script.StringAttr() { value = "\\e\\d\\c" });
            inputText.commands[0].attrList.Add(new Script.StringAttr() { value = "" });
            inputText.commands[0].attrList.Add(new Script.StringAttr() { value = "" });
            inputText.commands[0].attrList.Add(new Script.StringAttr() { value = "" });
            inputText.commands[0].attrList.Add(new Script.IntAttr() { value = 1 });
            _textBoxRunner = new ScriptRunner(mapScene, mapChr, inputText);

            if (!mapScene.runnerDic.ContainsKey(inputText.guId)) mapScene.runnerDic.Add(inputText.guId, _textBoxRunner);
        }

        private void SetBackgroundImg()
        {
            _backgroundContainer = catalog.getItemFromName<Texture>(_customBackground);
            if (_backgroundContainer != null)
            {
                _backgroundImage = _backgroundContainer.getTexture();
                Graphics.LoadImage(_backgroundContainer);
            }
            else
            {
                _backgroundContainer = catalog.getItemFromName<Texture>("ItemPopup");
                _backgroundImage = _backgroundContainer.getTexture();
                Graphics.LoadImage(_backgroundContainer);
                //  _backgroundImage.addRef();
            }

            if (_backgroundContainer == null) return;

            var sheet = mapChr.rom.sheetList.FirstOrDefault(x => x != null && x.Name == "Popup Cache");
            if (sheet == null) mapChr.rom.addNewSheet(catalog, "Popup Cache", Script.Trigger.TALK);

            sheet = mapChr.rom.sheetList.FirstOrDefault(x => x != null && x.Name == "Popup Cache");

            if (sheet.condList.Count == 0) sheet.condList.Add(new Yukar.Common.Rom.Event.Condition()
            { cond = Script.Command.ConditionType.EQUAL, local = false, pointer = -1, type = Yukar.Common.Rom.Event.Condition.Type.COND_TYPE_COLLISION, option = 0, index = 0, name = "", pointerName = "", refGuid = Guid.Empty });

            var script = catalog.getItemFromGuid(sheet.script) as Script;
            var cachedImage = script.commands.FirstOrDefault(x => x.type == Script.Command.FuncType.SPPICTURE);
            var cachedNote = script.commands.FirstOrDefault(x => x.type == Script.Command.FuncType.COMMENT);

            Script.GuidAttr imageGuid = new Script.GuidAttr(_backgroundContainer.guId);
            if (cachedNote == null)
            {
                script.commands.Add(new Script.Command() { type = Script.Command.FuncType.COMMENT });
                cachedNote = script.commands.FirstOrDefault(x => x.type == Script.Command.FuncType.COMMENT);
                cachedNote.attrList.Clear();
                cachedNote.attrList.Add(new Script.StringAttr() { value = "WARNING: Do not change the conditions or events on this page! You can edit any other page of this common event except this one!\r\n- If the script stopped working because you changed something here,\nyou just have to delete this page so that the script will create it automatically." });
            }
            if (cachedImage == null)
            {
                script.commands.Add(new Script.Command() { type = Script.Command.FuncType.SPPICTURE });
                cachedImage = script.commands.FirstOrDefault(x => x.type == Script.Command.FuncType.SPPICTURE);

                cachedImage.attrList.Clear();

                cachedImage.attrList.Add(new Script.IntAttr() { value = 0 });
                cachedImage.attrList.Add(imageGuid);
                cachedImage.attrList.Add(new Script.IntAttr() { value = 100 });
                cachedImage.attrList.Add(new Script.IntAttr() { value = -1 });
                cachedImage.attrList.Add(new Script.IntAttr() { value = 1 });
                cachedImage.attrList.Add(new Script.IntAttr() { value = 0 });
                cachedImage.attrList.Add(new Script.IntAttr() { value = 0 });
                cachedImage.attrList.Add(new Script.IntAttr() { value = -1 });
                cachedImage.attrList.Add(new Script.IntAttr() { value = 0 });
                cachedImage.attrList.Add(new Script.IntAttr() { value = 100 });
                cachedImage.attrList.Add(new Script.IntAttr() { value = 0 });
            }
            else
            {
                cachedImage.attrList[1] = imageGuid;
            }
        }
        private void SetSound()
        {
            _sound = catalog.getItemFromName<SoundResource>(_customSound);
            if (_sound != null)
            {
                _soundId = Audio.LoadSound(_sound);
            }
            else
            {
                _sound = catalog.getItemFromName<SoundResource>("ui085");
                _soundId = Audio.LoadSound(_sound);
            }

            if (_sound == null) return;

            var sheet = mapChr.rom.sheetList.FirstOrDefault(x => x != null && x.Name == "Popup Cache");
            if (sheet == null) mapChr.rom.addNewSheet(catalog, "Popup Cache", Script.Trigger.TALK);

            sheet = mapChr.rom.sheetList.FirstOrDefault(x => x != null && x.Name == "Popup Cache");
            var script = catalog.getItemFromGuid(sheet.script) as Script;
            if (sheet.condList.Count == 0) sheet.condList.Add(new Yukar.Common.Rom.Event.Condition()
            { cond = Script.Command.ConditionType.EQUAL, local = false, pointer = -1, type = Yukar.Common.Rom.Event.Condition.Type.COND_TYPE_COLLISION, option = 0, index = 0, name = "", pointerName = "", refGuid = Guid.Empty });
            var cachedSound = script.commands.FirstOrDefault(x => x.type == Script.Command.FuncType.PLAYSE);
            Script.GuidAttr soundGuid = new Script.GuidAttr(_sound.guId);
            var cachedNote = script.commands.FirstOrDefault(x => x.type == Script.Command.FuncType.COMMENT);
            if (cachedNote == null)
            {
                script.commands.Add(new Script.Command() { type = Script.Command.FuncType.COMMENT });
                cachedNote = script.commands.FirstOrDefault(x => x.type == Script.Command.FuncType.COMMENT);
                cachedNote.attrList.Clear();
                cachedNote.attrList.Add(new Script.StringAttr() { value = "WARNING: Do not change the conditions or events on this page! You can edit any other page of this common event except this one!\r\n- If the script stopped working because you changed something here,\nyou just have to delete this page so that the script will create it automatically." });
            }

            if (cachedSound == null)
            {

                script.commands.Add(new Script.Command() { type = Script.Command.FuncType.PLAYSE });
                cachedSound = script.commands.FirstOrDefault(x => x.type == Script.Command.FuncType.PLAYSE);
                cachedSound.attrList.Clear();
                cachedSound.attrList.Add(soundGuid);
            }
            else
            {
                cachedSound.attrList[0] = soundGuid;
            }

        }
        public override void Update()
        {
            _disablePlugin = mapScene.owner.data.system.GetSwitch(DisablePluginSwitch, Guid.Empty, false);
            if (_disablePlugin)
            {
                _inventory = mapScene.owner.data.party.Items;
                _previousInventory = mapScene.owner.data.party.Items;

                return;
            }


            EditorCheck();

            if (_time < _frequency)
            {
                _time += GameMain.getElapsedTime();
                return;
            }

            _inventory = mapScene.owner.data.party.Items;
            _addedItems = GetChanges(_inventory, _previousInventory, removedItems: false);
            _removedItems = GetChanges(_inventory, _previousInventory, removedItems: true);

            QueuePopup(_addedItems);
            QueuePopup(_removedItems, isRemoving: true);

            _previousInventory.Clear();
            _inventory.ForEach(x => _previousInventory.Add(new Party.ItemStack { item = x.item, enhancedItem = x.enhancedItem, num = x.num }));
            _addedItems.Clear();
            _time = 0f;

            // キャラクターが生存している間、
            // 毎フレームこのキャラクターのアップデート前にこのメソッドがコールされます。
            // This method is called every frame before this character updates while the character is alive.
        }

        private void EditorCheck()
        {
            if (Input.KeyTest(Input.StateType.TRIGGER, Input.KeyStates.ACTION3, Input.GameState.WALK) && mapScene.owner.IsTestPlayMode && _isEditMode)
            {
                if (mapScene.GetChoicesResult() == 0)
                {
                    _showingChoices = false;
                    return;
                }

                if (mapScene.IsVisibleChoices())
                {
                    mapScene.menuWindow.ResetLayout(LayoutProperties.LayoutNode.UsageInGame.Choice);
                }
                _isEditMode = !_isEditMode;
                if (_playerLocked)
                {
                    mapScene.UnlockControl();
                    _playerLocked = false;

                }
                _showingChoices = false;
                lastChoice = -1;
                _showingFunctionChoices = false;
                foreach (var guid in _toPauseList)
                {
                    if (mapScene.runnerDic.ContainsKey(guid))
                        mapScene.runnerDic[guid].state = ScriptRunner.ScriptState.Running;
                }

                foreach (var layout in layoutCacheList)
                    mapScene.owner.ShowFreeLayout(layout);

                layoutCacheList.Clear();
            }
            else if (Input.KeyTest(Input.StateType.DIRECT, Input.KeyStates.ACTION3, Input.GameState.WALK) && mapScene.owner.IsTestPlayMode && !_isEditMode)
            {
                if (editorTimer <= 1.5f)
                {
                    editorTimer += GameMain.getElapsedTime();
                    return;
                }

                editorTimer = 0;
                OpenEditorMode();
            }
            else if (Input.KeyTest(Input.StateType.TRIGGER_UP, Input.KeyStates.ACTION3, Input.GameState.WALK) && mapScene.owner.IsTestPlayMode && !_isEditMode)
            {
                editorTimer = 0;
            }

            if (_isEditMode) EditMode();
        }

        private void EditMode()
        {
            if (!_playerLocked)
            {
                mapScene.LockControl();
                _playerLocked = true;
            }

            if (!mapScene.IsVisibleChoices() && !_showingChoices)
            {
                mapScene.ShowChoices(new string[] { "Positions/Size", "Set background", "Set sound", "Set max notifications", "Enable/Disable Functions", "Save" }, 4);
                _showingChoices = true;
                lastChoice = -1;
            }


            if (lastChoice == -1)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.EVENT, "dada", (catalog.getGameSettings().screenWidth).ToString());
                Graphics.DrawString(0, $"Item Popup Editor", new Vector2((catalog.getGameSettings().screenWidth / 2) - 150, (catalog.getGameSettings().screenHeight / 4) - 100), Color.White, new Rectangle(catalog.getGameSettings().screenWidth / 2 - 150, (catalog.getGameSettings().screenHeight / 4 - 100), 1280, 200), 1f, 0);
                Graphics.DrawString(0, $"Press ACTION3 key to close editor mode", new Vector2((catalog.getGameSettings().screenWidth / 2) - 200, (catalog.getGameSettings().screenHeight) - 150), Color.White, new Rectangle(catalog.getGameSettings().screenWidth / 2 - 200, (catalog.getGameSettings().screenHeight - 150), 1280, 200), 0.7f, 0);

                lastChoice = mapScene.GetChoicesResult();
            }


            if (lastChoice == 1)
            {
                ShowTextBox(0);
            }
            else if (lastChoice == 2)
            {
                ShowTextBox(1);
            }
            else if (lastChoice == 3)
            {
                ShowTextBox(2);
            }
            else if (lastChoice == 4)
            {
                ShowFunctionsMenu();
            }
            else if (lastChoice == 5)
            {
                SaveChunkData();
                if (!mapScene.isToastVisible() && !_showingTextBox)
                {
                    mapScene.ShowToast("Settings saved!");
                    _showingTextBox = true;
                }

                if (!mapScene.isToastVisible())
                {
                    _showingChoices = false;
                    _showingTextBox = false;
                }
            }
            else if (lastChoice == 0)
            {
                if (Input.KeyTest(Input.StateType.TRIGGER_UP, Input.KeyStates.ACTION2, Input.GameState.WALK))
                {
                    _target++;
                    if (_target > 2) _target = 0;
                    if (_target == 0) _targetName = "Item Name";
                    else if (_target == 1) _targetName = "Item Icon";
                    else if (_target == 2) _targetName = "Background";
                }
                if (Input.KeyTest(Input.StateType.TRIGGER_UP, Input.KeyStates.DASH, Input.GameState.WALK))
                {
                    _mode++;
                    if (_mode > 2) _mode = 0;

                    if (_mode == 0)
                    {
                        _moveAmount = 6f;
                        _currentMode = "Position";
                    }
                    else if (_mode == 1)
                    {
                        _moveAmount = 1f;
                        _currentMode = "Position (Precision)";
                    }
                    else if (_mode == 2)
                    {
                        _currentMode = "Size";
                    }
                }

                if (Input.KeyTest(Input.StateType.DIRECT, Input.KeyStates.UP, Input.GameState.WALK))
                {
                    if (_mode < 2)
                    {
                        if (_target == 0)
                            _textPosition.Y -= _moveAmount * GameMain.getRelativeParam60FPS();
                        else if (_target == 1)
                            _iconPosition.Y -= _moveAmount * GameMain.getRelativeParam60FPS();
                        else if (_target == 2)
                            _backgroundPosition.Y -= _moveAmount * GameMain.getRelativeParam60FPS();

                    }
                    else if (_mode == 2)
                    {
                        if (_target == 1)
                            _iconSize.Y += GameMain.getElapsedTime() * 4f;
                        else if (_target == 2)
                            _backgroundSize.Y += GameMain.getElapsedTime() * 7f;
                    }
                }

                if (Input.KeyTest(Input.StateType.DIRECT, Input.KeyStates.DOWN, Input.GameState.WALK))
                {
                    if (_mode < 2)
                    {
                        if (_target == 0)
                            _textPosition.Y += _moveAmount * GameMain.getRelativeParam60FPS();
                        else if (_target == 1)
                            _iconPosition.Y += _moveAmount * GameMain.getRelativeParam60FPS();
                        else if (_target == 2)
                            _backgroundPosition.Y += _moveAmount * GameMain.getRelativeParam60FPS();
                    }
                    else if (_mode == 2)
                    {
                        if (_target == 1)
                            _iconSize.Y -= GameMain.getElapsedTime() * 4f;
                        else if (_target == 2)
                            _backgroundSize.Y -= GameMain.getElapsedTime() * 7f;
                    }

                }

                if (Input.KeyTest(Input.StateType.DIRECT, Input.KeyStates.RIGHT, Input.GameState.WALK))
                {
                    if (_mode < 2)
                    {
                        if (_target == 0)
                            _textPosition.X += _moveAmount * GameMain.getRelativeParam60FPS();
                        else if (_target == 1)
                            _iconPosition.X += _moveAmount * GameMain.getRelativeParam60FPS();
                        else if (_target == 2)
                            _backgroundPosition.X += _moveAmount * GameMain.getRelativeParam60FPS();
                    }
                    else if (_mode == 2)
                    {
                        if (_target == 0)
                            _fontSize += GameMain.getElapsedTime() * 1f;
                        else if (_target == 1)
                            _iconSize.X += GameMain.getElapsedTime() * 4f;
                        else if (_target == 2)
                            _backgroundSize.X += GameMain.getElapsedTime() * 7f;
                    }


                }
                if (Input.KeyTest(Input.StateType.DIRECT, Input.KeyStates.LEFT, Input.GameState.WALK))
                {
                    if (_mode < 2)
                    {
                        if (_target == 0)
                            _textPosition.X -= _moveAmount * GameMain.getRelativeParam60FPS();
                        else if (_target == 1)
                            _iconPosition.X -= _moveAmount * GameMain.getRelativeParam60FPS();
                        else if (_target == 2)
                            _backgroundPosition.X -= _moveAmount * GameMain.getRelativeParam60FPS();
                    }
                    else if (_mode == 2)
                    {
                        if (_target == 0)
                            _fontSize -= GameMain.getElapsedTime() * 1f;
                        else if (_target == 1)
                            _iconSize.X -= GameMain.getElapsedTime() * 4f;
                        else if (_target == 2)
                            _backgroundSize.X -= GameMain.getElapsedTime() * 7f;
                    }

                }
                //if (Input.KeyTest(Input.StateType.TRIGGER_UP, Input.KeyStates.ACTION1, Input.GameState.WALK))
                //{
                //    SaveChunkData();
                //    mapScene.ShowToast("Settings saved!");
                //    //  SetClipboard();
                //}

                ItemDrawEditorMode();
            }
        }

        private void ShowFunctionsMenu()
        {
            if (!mapScene.IsVisibleChoices() && !_showingFunctionChoices)
            {
                mapScene.ShowChoices(new string[] { $"Use +/- symbols: {_useAddMinus}", $"Use background: {_useBackground}", $"Use sound: {_useSound}", $"Use item icon: {_useItemIcon}", $"Use Popup color: {_usePopupColor}", "Back" }, 4);
                _showingFunctionChoices = true;
            }

            var choice = mapScene.GetChoicesResult();

            if (choice == -1)
            {
                return;
            }


            if (choice == 0) _useAddMinus = !_useAddMinus;
            else if (choice == 1) _useBackground = !_useBackground;
            else if (choice == 2) _useSound = !_useSound;
            else if (choice == 3) _useItemIcon = !_useItemIcon;
            else if (choice == 4) _usePopupColor = !_usePopupColor;
            else if (choice == 5)
            {
                _showingChoices = false;
                _showingFunctionChoices = false;
                return;
            }

            _showingFunctionChoices = false;
        }

        private void ShowTextBox(int target)
        {

            if (_textBoxRunner.state != ScriptRunner.ScriptState.Running && !_showingTextBox)
            {
                if (target == 0) mapScene.owner.data.system.SetVariable("popupTxtTemp", _customBackground, Guid.Empty, false);
                else if (target == 1) mapScene.owner.data.system.SetVariable("popupTxtTemp", _customSound, Guid.Empty, false);
                else if (target == 2) mapScene.owner.data.system.SetVariable("popupTxtTemp", _maxItemsToShow, Guid.Empty, false);

                _showingTextBox = true;
                _textBoxRunner.Run();
            }

            if (!_textBoxRunner.isFinished()) return;

            _showingChoices = false;
            _showingTextBox = false;

            if (target == 0)
            {
                _customBackground = mapScene.owner.data.system.GetStrVariable("popupTxtTemp", Guid.Empty, false);
                SetBackgroundImg();
            }
            else if (target == 1)
            {
                _customSound = mapScene.owner.data.system.GetStrVariable("popupTxtTemp", Guid.Empty, false);
                SetSound();
            }
            else if (target == 2)
            {

                int.TryParse(mapScene.owner.data.system.GetStrVariable("popupTxtTemp", Guid.Empty, false), out int result);

                if (result > 0)
                {
                    _maxItemsToShow = result;
                }
            }
        }

        private void QueuePopup(List<Party.ItemStack> itemList, bool isRemoving = false)
        {
            UpdateSettings(isRemoving);

            List<Party.ItemStack> itemsToIterate = new List<Party.ItemStack>();
            itemList.ForEach(x => itemsToIterate.Add(new Party.ItemStack { item = x.item, enhancedItem = x.enhancedItem, num = x.num }));

            foreach (Party.ItemStack item in itemsToIterate)
            {
                ItemDrawer itemDrawer = new ItemDrawer(item, _symbol, _textColor);

                _itemsToDraw.Add(itemDrawer);
            }
        }

        private void UpdateSettings(bool isRemoving)
        {
            _symbol = _useAddMinus ? (isRemoving ? "-" : "+") : "x";
            _textColor = _usePopupColor ? (isRemoving ? _removingColor : _addingColor) : Color.White;
        }

        public override void BeforeUpdate()
        {
            // キャラクターが生存している間、
            // 毎フレーム、イベント内容の実行前にこのメソッドがコールされます。
            // This method will be called every frame while the character is alive, before the event content is executed.
        }

        public override void Destroy()
        {
            // if (!mapScene.owner.IsTestPlayMode) SaveChunk();
            // キャラクターが破棄される時に、このメソッドがコールされます。
            // This method is called when the character is destroyed.
        }

        public override void AfterDraw()
        {
            ItemDraw();
            // このフレームの2D描画処理の最後に、このメソッドがコールされます。
            // This method is called at the end of the 2D drawing process for this frame.
        }
        private void ItemDraw()
        {

            for (int i = 0; i < _itemsToDraw.Count; i++)
            {
                if (_itemsToDraw[i].ItemStack.item.tags.ToLower().Contains(NotPopupTag))
                {
                    _itemsToRemove.Add(_itemsToDraw[i]);
                    continue;
                }

                if (!_itemsToDraw[i].SoundPlayed && _useSound)
                {
                    _itemsToDraw[i].SoundPlayed = true;

                    Audio.PlaySound(_soundId, 0, 0.85f, 1, Audio.SoundType.Normal);
                }

                if (i >= _maxItemsToShow)
                {
                    continue;
                }



                _itemsToDraw[i].Time += GameMain.getElapsedTime();

                var rect = new Rectangle((int)_textPosition.X, (int)_textPosition.Y + (i * _popupOffset), (int)(_backgroundSize.X), (int)(_backgroundSize.Y));

                if (_useBackground) Graphics.DrawImage(_backgroundImage, (int)_backgroundPosition.X, (int)_backgroundPosition.Y + (i * _popupOffset), (int)_backgroundSize.X, (int)_backgroundSize.Y, 0);
                if (_useItemIcon) Graphics.DrawChipImageSpecifiedSize(_itemsToDraw[i].IconTex, (int)_iconPosition.X, (int)_iconPosition.Y + (i * _popupOffset), _itemsToDraw[i].Icon.x, _itemsToDraw[i].Icon.y, (int)_iconSize.X, (int)_iconSize.Y, byte.MaxValue, byte.MaxValue, byte.MaxValue, 220, 0);
                Graphics.DrawString(0, $"{_itemsToDraw[i].ItemStack.item.name}  {_itemsToDraw[i].Symbol}{_itemsToDraw[i].ItemStack.num}", new Vector2(_textPosition.X, _textPosition.Y + (i * _popupOffset)), _itemsToDraw[i].TextColor, rect, _fontSize, 0);


                if (_itemsToDraw[i].Time >= 2.5f)
                {
                    _itemsToRemove.Add(_itemsToDraw[i]);
                    continue;
                }

                //if (_useBackground) Graphics.DrawImage(_backgroundImage, (int)_textPosition.X - (int)(15 + _iconSize.X), (int)_textPosition.Y + (i * _popupOffset), (int)_backgroundSize.X, (int)_backgroundSize.Y, 0);
                //if (_useItemIcon) Graphics.DrawChipImageSpecifiedSize(_itemsToDraw[i].IconTex, (int)_iconPosition.X, (int)_iconPosition.Y + (i * _popupOffset), _itemsToDraw[i].Icon.x, _itemsToDraw[i].Icon.y, (int)_iconSize.X, (int)_iconSize.Y, byte.MaxValue, byte.MaxValue, byte.MaxValue, 220, 0);
                //Graphics.DrawString(0, $"{_itemsToDraw[i].ItemStack.item.name}  {_itemsToDraw[i].Symbol}{_itemsToDraw[i].ItemStack.num}", new Vector2(_textPosition.X, _textPosition.Y + (i * _popupOffset)), _itemsToDraw[i].TextColor, rect, _fontSize, 0);

            }
            foreach (var item in _itemsToRemove)
            {
                _itemsToDraw.Remove(item);
                // item.IconTex.getTexture().removeRef();
            }

            _itemsToRemove.Clear();
        }

        private List<Party.ItemStack> GetChanges(List<Party.ItemStack> main, List<Party.ItemStack> secondary, bool removedItems)
        {
            if (removedItems)
            {
                var temp = new List<Party.ItemStack>();
                temp.AddRange(main);
                main = secondary;
                secondary = temp;
            }

            List<Party.ItemStack> changes = new List<Party.ItemStack>();

            foreach (var item in main)
            {
                var currentItem = secondary.FirstOrDefault(x => x.item.guId == item.item.guId);
                if (currentItem == null)
                {
                    changes.Add(item);
                }
                else if (currentItem.num < item.num)
                {
                    Party.ItemStack temp = new Party.ItemStack()
                    {
                        item = item.item,
                        enhancedItem = item.enhancedItem,
                        num = Math.Abs(item.num - currentItem.num)
                    };

                    changes.Add(temp);
                }
            }

            return changes;
        }

        // Call Functions:
        [BakinFunction(Description = "Enable/disable plugin \n 1. Disable \n 0. Enable")]
        public void DisablePlugin(int intBool)
        {
            if (intBool < 0) intBool = 0;
            _disablePlugin = intBool != 0;

            mapScene.owner.data.system.SetSwitch(DisablePluginSwitch, intBool != 0, Guid.Empty, false);
        }

        [BakinFunction(Description = "Use \"+\" and \"-\" instead of the \"x\" symbol \n 1. Enable\n 0. Disable")]
        public void UseAddMinusSimbols(int intBool)
        {
            if (intBool < 0) intBool = 0;
            _useAddMinus = intBool != 0;
        }

        [BakinFunction(Description = "Use popup color when adding or removing items \n 1. Enable\n 0. Disable")]
        public void UsePopupColor(int intBool)
        {
            if (intBool < 0) intBool = 0;
            _usePopupColor = intBool != 0;
        }

        [BakinFunction(Description = "Show item icon \n 1. Enable\n 0. Disable")]
        public void UseShowItemIcon(int intBool)
        {
            if (intBool < 0) intBool = 0;
            _useItemIcon = intBool != 0;
        }

        [BakinFunction(Description = "Use a backgrund image when popup \n 1. Enable\n 0. Disable")]
        public void UseBackgroundImage(int intBool)
        {
            if (intBool < 0) intBool = 0;
            _useBackground = intBool != 0;
        }

        [BakinFunction(Description = "Use a sound when popup \n 1. Enable\n 0. Disable")]
        public void UseSound(int intBool)
        {
            if (intBool < 0) intBool = 0;
            _useSound = intBool != 0;
        }

        [BakinFunction(Description = "Change: text X position")]
        public void TextPositionX(float x)
        {
            _textPosition.X = x;
        }

        [BakinFunction(Description = "Change: text Y position")]
        public void TextPositionY(float y)
        {
            _textPosition.Y = y;
        }
        [BakinFunction(Description = "Change: icon X position")]
        public void IconPositionX(float X)
        {
            _iconPosition.X = X;
        }
        [BakinFunction(Description = "Change: icon Y position")]
        public void IconPositionY(float Y)
        {
            _iconPosition.Y = Y;
        }

        [BakinFunction(Description = "Change: Background Image \n Case sensitive")]
        public void SetBackgroundImage(string backgroundName)
        {
            _customBackground = backgroundName;
            SetBackgroundImg();
        }

        [BakinFunction(Description = "Change: Sound \n Case sensitive")]
        public void SetSound(string soundName)
        {
            _customSound = soundName;
            SetSound();
        }

        [BakinFunction(Description = "Change: popup offset between notifications")]
        public void PopupOffset(float value)
        {
            _popupOffset = (int)value;
        }

        [BakinFunction(Description = "Change: set the maximun item number to show at the same time")]
        public void SetMaxNotifications(float value)
        {
            _maxItemsToShow = (int)value;
        }

        [BakinFunction(Description = "Open Editor mode \n Use jump key to exit")]
        public void OpenEditorMode()
        {

            _isEditMode = !_isEditMode;
            if (_playerLocked)
            {
                mapScene.UnlockControl();
                _playerLocked = false;
            }

            foreach (ScriptRunner item in mapScene.runnerDic.getList())
            {
                if (item.state == ScriptRunner.ScriptState.Running)
                {
                    if (!_toPauseList.Contains(item.key)) _toPauseList.Add(item.key);
                    item.state = ScriptRunner.ScriptState.DeepPaused;
                }
            }

            foreach (var layout in mapScene.owner.freeLayouts)
            {
                if (layout.IsVisibleLayout())
                {
                    layoutCacheList.Add(layout.LayoutGuid);
                }
            }
            mapScene.owner.HideALLFreeLayout(false);

        }
        private void ItemDrawEditorMode()
        {
            var rect = new Rectangle((int)_textPosition.X, (int)_textPosition.Y + (0 * _popupOffset), (int)(_backgroundSize.X), (int)(_backgroundSize.Y));


            Graphics.DrawString(0, $"Text Pos X: {(int)_textPosition.X} - Text Pos Y: {(int)_textPosition.Y} - Text Scale: {_fontSize}", new Vector2(50, 50), Color.White, new Rectangle(50, 50, 1280, 200), 0.8f, 0);
            Graphics.DrawString(0, $"Icon Pos X: {(int)_iconPosition.X} - Icon Pos Y: {(int)_iconPosition.Y} - Icon Size X: {(int)_iconSize.X} - Icon Size Y: {(int)_iconSize.Y} ", new Vector2(50, 80), Color.White, new Rectangle(50, 80, 1280, 200), 0.8f, 0);
            Graphics.DrawString(0, $"Background Pos X: {(int)_backgroundPosition.X} - Background Pos Y: {(int)_backgroundPosition.Y} - Background Size X: {(int)_backgroundSize.X} - Background Size Y: {(int)_backgroundSize.Y}", new Vector2(50, 110), Color.White, new Rectangle(50, 110, 1280, 200), 0.8f, 0);

            Graphics.DrawString(0, $"Mode: {_currentMode} - (Dash/Run Key)", new Vector2(50, 200), Color.White);
            Graphics.DrawString(0, $"Current target: {_targetName} - (Action2 Key - default: Ctrl)", new Vector2(50, 230), Color.White);
            //  Graphics.DrawString(0, $"Save Settings to project (Action1 Key - default: Space/Z)", new Vector2(50, 280), Color.White);

            Graphics.DrawString(0, $"Back to menu (Action3 Key - default: X)", new Vector2(50, 310), Color.White);

            Graphics.DrawImage(_backgroundImage, (int)_backgroundPosition.X, (int)_backgroundPosition.Y + (0 * _popupOffset), (int)_backgroundSize.X, (int)_backgroundSize.Y, 0);
            Graphics.DrawChipImageSpecifiedSize(_editorItem.IconTex, (int)_iconPosition.X, (int)_iconPosition.Y + (0 * _popupOffset), _editorItem.Icon.x, _editorItem.Icon.y, (int)_iconSize.X, (int)_iconSize.Y, byte.MaxValue, byte.MaxValue, byte.MaxValue, 220, 0);
            Graphics.DrawString(0, $"{_editorItem.ItemStack.item.name}  {_editorItem.Symbol}{_editorItem.ItemStack.num}", new Vector2(_textPosition.X, _textPosition.Y + (0 * _popupOffset)), _editorItem.TextColor, rect, _fontSize, 0);
            // Graphics.DrawRect((int)textPosition.X, (int)textPosition.Y + (0 * popupOffset),(int)(backgroundSize.X * fontSize), (int)(backgroundSize.Y * fontSize), byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue, 0);
        }

        private void SetClipboard()
        {
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.AppendLine($"Text Position X: {_textPosition.X} - Text Position Y: {_textPosition.Y} - Text Scale: {_fontSize}");
            stringBuilder.AppendLine($"Icon Position X: {_iconPosition.X} - Icon Position Y: {_iconPosition.X} - Icon Size X: {_iconSize.X} - Icon Size Y: {_iconSize.Y} ");
            stringBuilder.AppendLine($"Background Position X: {_backgroundPosition.X} - Background Position Y: {_backgroundPosition.Y} - Background Size X: {_backgroundSize.X} - Background Size Y: {_backgroundSize.Y}");

            Clipboard.SetText(stringBuilder.ToString());
        }

        private void LoadChunkData()
        {
            try
            {
                var chunkList = catalog.getFilteredExtraChunkList(guid);

                if (chunkList.Count > 0)
                {
                    _usingChunk = true;
                    chunkList[0].readChunk(settings);
                }

                if (_usingChunk) AssignData();
            }
            catch (Exception ex)
            {
                GameMain.PushLog(DebugDialog.LogEntry.LogType.EVENT, "Item Popup", $"Corrupted data, recreating data \n {ex}");
                var chunkList = catalog.getFilteredExtraChunkList(guid);

                if (chunkList.Count > 0)
                {
                    catalog.deleteItem(chunkList[0]);
                }
                settings = new PopupSettings();
                SaveChunkData();
            }

        }

        private void AssignData()
        {
            _disablePlugin = settings.disablePlugin;

            _useAddMinus = settings.useAddMinus;
            _useItemIcon = settings.useItemIcon;
            _useSound = settings.useSound;
            _usePopupColor = settings.usePopupColor;
            _useBackground = settings.useBackground;

            _textPosition.X = settings.textPositionX;
            _textPosition.Y = settings.textPositionY;
            _fontSize = settings.fontSize;

            _iconPosition.X = settings.iconPositionX;
            _iconPosition.Y = settings.iconPositionY;
            _iconSize.X = settings.iconSizeX;
            _iconSize.Y = settings.iconSizeY;

            _customBackground = settings.customBackground;
            _backgroundPosition.X = settings.backgroundPositionX;
            _backgroundPosition.Y = settings.backgroundPositionY;
            _backgroundSize.X = settings.backgroundSizeX;
            _backgroundSize.Y = settings.backgroundSizeY;

            _popupOffset = settings.popupOffset;

            _textColor = ParseRgbString(settings.textColor);
            _addingColor = ParseRgbString(settings.addingColor);
            _removingColor = ParseRgbString(settings.removingColor);

            _customSound = settings.customSound;
            _maxItemsToShow = settings.maxItemsToShow;

        }


        private void SaveChunkData()
        {
            settings.disablePlugin = _disablePlugin;

            settings.useAddMinus = _useAddMinus;
            settings.useItemIcon = _useItemIcon;
            settings.useSound = _useSound;
            settings.usePopupColor = _usePopupColor;
            settings.useBackground = _useBackground;

            settings.textPositionX = (int)_textPosition.X;
            settings.textPositionY = (int)_textPosition.Y;
            settings.fontSize = _fontSize;

            settings.iconPositionX = (int)_iconPosition.X;
            settings.iconPositionY = (int)_iconPosition.Y;
            settings.iconSizeX = (int)_iconSize.X;
            settings.iconSizeY = (int)_iconSize.Y;

            settings.customBackground = _customBackground;
            settings.backgroundPositionX = (int)_backgroundPosition.X;
            settings.backgroundPositionY = (int)_backgroundPosition.Y;
            settings.backgroundSizeX = (int)_backgroundSize.X;
            settings.backgroundSizeY = (int)_backgroundSize.Y;

            settings.popupOffset = _popupOffset;

            settings.textColor = ColorToRgbString(_textColor);
            settings.addingColor = ColorToRgbString(_addingColor);
            settings.removingColor = ColorToRgbString(_removingColor);

            settings.customSound = _customSound;
            settings.maxItemsToShow = _maxItemsToShow;

            SaveChunk();
        }
        private void SaveChunk()
        {
            ExtraChunk rom;
            var entries = catalog.getFilteredExtraChunkList(guid);
            if (entries.Count == 0)
            {
                rom = new ExtraChunk(guid);

                catalog.addItem(rom, Catalog.OVERWRITE_RULES.ALWAYS_BUT_DONT_CHANGE_ORDER);
            }
            else
            {
                rom = entries[0];
            }
            rom.writeChunk(settings);
        }
        private static Color ParseRgbString(string rgb)
        {
            string[] rgbValues = rgb.Split(',');
            if (rgbValues.Length != 3)
            {
                return new Color(255, 255, 255);
            }

            int red = int.Parse(rgbValues[0]);
            int green = int.Parse(rgbValues[1]);
            int blue = int.Parse(rgbValues[2]);

            red = Math.Max(0, Math.Min(255, red));
            green = Math.Max(0, Math.Min(255, green));
            blue = Math.Max(0, Math.Min(255, blue));

            return new Color(red, green, blue);
        }
        private static string ColorToRgbString(Color color)
        {
            return $"{color.R},{color.G},{color.B}";
        }
    }
}
