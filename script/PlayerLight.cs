using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Yukar.Common;
using Yukar.Common.Rom;
using Yukar.Engine;
namespace Bakin
{
    public class PlayerLight : BakinObject
    {
        // ------------------------------
        // Player Light Plugin V 1.0.1
        // By Jagonz  
        // Follow me in https://jagonz.itch.io !
        // Youtube: https://www.youtube.com/@JagonzBakin
        // Twitter:  
        // ------------------------------



        // Settings ==========================================================================================
        //
        // All the settings here will be used only in case you don't have the "Player light" plugin installed
        //
        // ===================================================================================================
        private Settings settings = new Settings();

        private bool useScriptSettings = true; // Update the player light when the map is loaded with the script light settings. (Overwritten by useCustomLightSettings)
        private string bakinDisableSwitch = "DisablePlayerLight";
        private string bakinSettingsToUse = "light settings";
        private bool cleanLights = false;
        // FpView support
        private bool usingFpView = false;  // If you want to use player light with "Fpview" script, change this value to true!
        private float fpLightOffset = 0.4f; // You can change the FIRST PERSON light offset to left (negative) or right (positive) by changing this value.
        private string bakinVarFpOffset = "fp offset";

        // Light settings ========= These values will be overwritten if you use custom light settings  ========= //

        private string lightName = "playerLight";
        private float lightHeight = 1.5f;
        private Color lightColor = Color.FromArgb(255, 255, 255, 255); // (Alpha, R , G , B)                                                     
        private float intensity = 0.6f;
        private float innerAngle = 30f;
        private float outerAngle = 40f;
        private int lightType = 1;  // 0 = Point , 1 = Spot
        private float radius = 8f;
        private float specular = 1f;
        private float lightAngleX = -45f; // Only affects "SpotLight" 
        private float lightAngleY = 0f;
        private float lightAngleZ = 0f;

        // =================================================== //

        // ========= Bakin variable names to customize the light in game ========= //

        private bool useCustomLightSettings = false; // If you change this field to "true" you must setup your light settings in the following bakin variables:


        private string bakinVarHeight = "light Height";
        private string bakinVarColor = "light Color"; // Must be an array where indexes are:  (0 = Alpha, 1 = Red, 2 = Green, 3 = Blue)
        private string bakinVarIntensity = "light Intensity";
        private string bakinVarInnerAngle = "light InnerAngle";
        private string bakinVarOuterAngle = "light OuterAngle";
        private string bakinVarLightType = "light Type";
        private string bakinVarRadius = "light Radius";
        private string bakinVarSpecular = "light Specular";
        private string staticToVars;
        private string bakinVarAngleX = "light AngleX";
        private string bakinVarAngleY = "light AngleY";
        private string bakinVarAngleZ = "light AngleZ";

        // =================================================== //


        private MapCharacter mainCharacter;
        private Map.LocalLight playerLight;
        private string currentMap;

        private List<Map> mapsWithLights = new List<Map>();
        private bool disablePlugin = false;
        private SettingsMethod settingsMethod;
        private Guid chunkId = new Guid("B2E81DA0-0B86-48CD-A872-2A78BA24C2CE"); // never change this
        private string settingsType = "default";

        public override void Start()
        {
            ChunkOperations();
            // キャラクターが生成される時に、このメソッドがコールされます。
            // This method is called when the character is created.
        }

        public override void Update()
        {
           

            // キャラクターが生存している間、
            // 毎フレームこのキャラクターのアップデート前にこのメソッドがコールされます。
            // This method is called every frame before this character updates while the character is alive.
        }

        public override void BeforeUpdate()
        {
            // キャラクターが生存している間、
            // 毎フレーム、イベント内容の実行前にこのメソッドがコールされます。
            // This method will be called every frame while the character is alive, before the event content is executed.
        }
        private void ChunkOperations()
        {

            var entries = catalog.getFilteredExtraChunkList(chunkId);
            if (entries.Count == 0) return;

            GameMain.PushLog(DebugDialog.LogEntry.LogType.EVENT, "PlayerLight", "loading stored settings");
            entries[0].readChunk(settings);
            DataAssignment();
        }
        private void DataAssignment()
        {
            disablePlugin = settings.disablePlugin;
            settingsMethod = (SettingsMethod)settings.settingMethod;
            switch (settingsMethod)
            {
                case SettingsMethod.StaticMethod:
                    useScriptSettings = true;
                    useCustomLightSettings = false;
                    settingsType = "";
                    break;

                case SettingsMethod.CustomMethod:
                    useScriptSettings = false;
                    useCustomLightSettings = true;
                    settingsType = "";
                    break;

                case SettingsMethod.SetAtRuntime:
                    useScriptSettings = false;
                    useCustomLightSettings = false;
                    break;
            }

            usingFpView = settings.usingFpView;
            cleanLights = settings.cleanLights;
            bakinDisableSwitch = settings.disableSwitch;
            bakinSettingsToUse = settings.settingsToUse;

            lightType = settings.lightType;
            lightHeight = settings.height;
            intensity = settings.intensity;
            radius = settings.radius;
            innerAngle = settings.innerAngle;
            outerAngle = settings.outerAngle;
            lightAngleX = settings.angleX;
            lightAngleY = settings.angleY;
            lightAngleZ = settings.angleZ;

            lightColor = System.Drawing.Color.FromArgb(settings.argb[0], settings.argb[1], settings.argb[2], settings.argb[3]);

            bakinVarLightType = settings.txtLightType;
            bakinVarHeight = settings.txtHeight;
            bakinVarIntensity = settings.txtIntensity;
            bakinVarRadius = settings.txtRadius;
            bakinVarInnerAngle = settings.txtInnerAngle;
            bakinVarOuterAngle = settings.txtOuterAngle;
            bakinVarAngleX = settings.txtAngleX;
            bakinVarAngleY = settings.txtAngleY;
            bakinVarAngleZ = settings.txtAngleZ;
            bakinVarColor = settings.txtColor;

            fpLightOffset = settings.numFpOffset;
            bakinVarFpOffset = settings.txtFpOffset;

            specular = settings.numSpecular;
            bakinVarSpecular = settings.txtSpecular;

            staticToVars = settings.txtStaticToVars;
        }
        public override void Destroy()
        {
            if (!cleanLights) return;
            try
            {
                foreach (var aMap in mapsWithLights)
                {
                    List<Map.LocalLight> lights = aMap.getLocalLights();

                    for (int i = 0; i < lights.Count; i++)
                    {
                        if (lights[i].name.Equals(lightName))
                            aMap.removeLocalLight(lights[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            // キャラクターが破棄される時に、このメソッドがコールされます。
            // This method is called when the character is destroyed.
        }

        public override void AfterDraw()
        {

            if (mapScene == null && mapChr.isCommonEvent)
            {
                mapScene = GameMain.instance.mapScene;
                return;
            }

            if (disablePlugin) return;

            if (mapScene.owner.data.system.GetSwitch(staticToVars))
            {
                CopyStaticToVars();

                mapScene.owner.data.system.SetSwitch(staticToVars, false);
            }

            var mapData = mapScene.mapDrawer.mapRom;
            if (mapData.Name != currentMap)
            {
                if (!mapsWithLights.Contains(mapData)) mapsWithLights.Add(mapData);
                mainCharacter = null;
                playerLight = null;
            }

            if (mainCharacter == null|| mainCharacter != mapScene.GetHero()) mainCharacter = mapScene.GetHero();

            if (playerLight == null) InitializeLight();
            else
            {
                if (settingsMethod == SettingsMethod.SetAtRuntime)
                {
                    settingsType = GetString(bakinSettingsToUse).ToLower();
                }

                if (useCustomLightSettings || settingsType == "custom" || settingsType == "1")
                {

                    UpdateLight(ColorCreator(bakinVarColor), GetVariable(bakinVarInnerAngle), GetVariable(bakinVarOuterAngle), GetVariable(bakinVarIntensity), (int)GetVariable(bakinVarLightType),
                                           GetVariable(bakinVarRadius), GetVariable(bakinVarSpecular), GetVariable(bakinVarAngleX), GetVariable(bakinVarAngleY), GetVariable(bakinVarAngleZ));

                    lightHeight = GetVariable(bakinVarHeight);
                    fpLightOffset = GetVariable(bakinVarFpOffset);
                }
                else if (useScriptSettings || settingsType == "default" || settingsType == "0")
                {
                    UpdateLight(lightColor, innerAngle, outerAngle, intensity, lightType,
                                                radius, specular, lightAngleX, lightAngleY, lightAngleZ);
                    lightHeight = settings.height;
                    fpLightOffset = settings.numFpOffset;
                }

                LightFunction();

            }

            // このフレームの2D描画処理の最後に、このメソッドがコールされます。
            // This method is called at the end of the 2D drawing process for this frame.
        }

        private void CopyStaticToVars()
        {
            var bakinSystem = mapScene.owner.data.system;
            bakinSystem.SetVariable(bakinVarLightType, lightType);
            bakinSystem.SetVariable(bakinVarIntensity, intensity);
            bakinSystem.SetVariable(bakinVarHeight, lightHeight);
            bakinSystem.SetVariable(bakinVarRadius, radius);
            bakinSystem.SetVariable(bakinVarSpecular, specular);
            bakinSystem.SetVariable(bakinVarInnerAngle, innerAngle);
            bakinSystem.SetVariable(bakinVarOuterAngle, outerAngle);
            bakinSystem.SetVariable(bakinVarAngleX, lightAngleX);
            bakinSystem.SetVariable(bakinVarAngleY, lightAngleY);
            bakinSystem.SetVariable(bakinVarAngleZ, lightAngleZ);
            bakinSystem.SetToArray(bakinVarColor, 0, lightColor.A);
            bakinSystem.SetToArray(bakinVarColor, 1, lightColor.R);
            bakinSystem.SetToArray(bakinVarColor, 2, lightColor.G);
            bakinSystem.SetToArray(bakinVarColor, 3, lightColor.B);
            bakinSystem.SetVariable(bakinVarFpOffset, fpLightOffset);
        
        }

        private void LightFunction()
        {
            if (mainCharacter == null) return;
            playerLight.visibility = !mapScene.isBattle && !mapScene.owner.data.system.GetSwitch(bakinDisableSwitch) && !disablePlugin;
            playerLight.setPosition(mainCharacter.getPosition());
            playerLight.PosY = mainCharacter.getPosition().Y + lightHeight;

            if (!usingFpView)
            {
                playerLight.RotY = mainCharacter.getRotation().Y - 180f;
                return;
            }

            playerLight.RotY = mapScene.yCurrentAngle;
            playerLight.RotX = mapScene.xCurrentAngle;
            playerLight.PosX = mainCharacter.getPosition().X + (mainCharacter.getCurDir().Z * fpLightOffset) * -1;
            playerLight.PosZ = mainCharacter.getPosition().Z + (mainCharacter.getCurDir().X * fpLightOffset) * 1;
        }

        private void InitializeLight()
        {
            var light = mapScene.mapDrawer.mapRom.getLocalLights().Find(x => x.name.Equals(lightName));

            if (light != null)
            {
                currentMap = mapScene.mapDrawer.mapRom.Name;
                playerLight = light;
                return;
            }
            var newLight = CreateLight(lightName, lightColor, innerAngle, outerAngle, intensity, lightType, radius, specular, lightAngleX, lightAngleY, lightAngleZ);
            mapScene.mapDrawer.mapRom.addLocalLight(newLight);
            mapScene.setLightDisplayID(false, GameMain.sDefaultDisplayID);
        }

        private Map.LocalLight CreateLight(string name, Color Color, float inAngle, float outAngle, float intensity, int type,
                                           float radius, float specular, float X, float Y, float Z)
        {
            Map.LocalLight newLight = new Map.LocalLight(mapScene.mapDrawer.mapRom)
            {
                Intensity = intensity,
                InnerAngle = inAngle,
                OuterAngle = outAngle,
                Name = name,
                Color = Color,
                Type = type,
                Radius = radius,
                Specular = specular,
                RotX = X,
                RotY = Y,
                RotZ = Z,
                visibility = true
            };

            return newLight;
        }

        private void UpdateLight(Color Color, float inAngle, float outAngle, float intensity, int type,
                               float radius, float specular, float X, float Y, float Z)
        {
            playerLight.Color = Color;
            playerLight.Intensity = intensity;
            playerLight.InnerAngle = inAngle;
            playerLight.OuterAngle = outAngle;
            playerLight.Type = type;
            playerLight.Radius = radius;
            playerLight.Specular = specular;
            playerLight.RotX = X;
            playerLight.RotY = Y;
            playerLight.RotZ = Z;

        }

        private float GetVariable(string name)
        {

            return (float)mapScene.owner.data.system.GetVariable(name);

        }
        private string GetString(string name)
        {

            return mapScene.owner.data.system.GetStrVariable(name, Guid.Empty, false);

        }

        private Color ColorCreator(string varName)
        {
            int[] color = { 255, 255, 255, 255 };
            if (mapScene.owner.data.system.VariableArrays.ContainsKey(bakinVarColor))
            {
                for (int i = 0; i < color.Length; i++)
                {
                    mapScene.owner.data.system.VariableArrays[varName].values.TryGetValue(i, out Yukar.Common.GameData.Variable value);
                    color[i] = (int)value.getDouble();
                }
            }


            return Color.FromArgb(color[0], color[1], color[2], color[3]);
        }

        enum SettingsMethod
        {
            StaticMethod = 0,
            CustomMethod = 1,
            SetAtRuntime = 2
        }
        internal class Settings : IChunk
        {
            public bool disablePlugin = false;
            public int settingMethod = 0;
            public bool usingFpView = false;
            public bool cleanLights = false;
            public string disableSwitch = "disable PlayerLight";
            public string settingsToUse = "light settings";

            public int lightType = 0;
            public float height = 1.5f;
            public float intensity = 0.7f;
            public float radius = 8f;
            public float innerAngle = 30f;
            public float outerAngle = 40f;
            public float angleX = -45f;
            public float angleY = 0;
            public float angleZ = 0;
            public int[] argb = new int[] { 255, 255, 255, 192 };

            public string txtLightType = "light Type";
            public string txtHeight = "light Height";
            public string txtIntensity = "light Intensity";
            public string txtRadius = "light Radius";
            public string txtInnerAngle = "light InnerAngle";
            public string txtOuterAngle = "light OuterAngle";
            public string txtAngleX = "light AngleX";
            public string txtAngleY = "light AngleY";
            public string txtAngleZ = "light AngleZ";
            public string txtColor = "light Color";

            // 1.0.1
            public float numFpOffset = 0.4f;
            public string txtFpOffset = "fp offset";
            public float numSpecular = 1f;
            public string txtSpecular = "light Specular";
            public string txtStaticToVars = "light staticToVars";

            public void load(BinaryReader reader)
            {
                disablePlugin = reader.ReadBoolean();
                settingMethod = reader.ReadInt32();
                usingFpView = reader.ReadBoolean();
                cleanLights = reader.ReadBoolean();
                disableSwitch = reader.ReadString();
                settingsToUse = reader.ReadString();

                lightType = reader.ReadInt32();
                height = reader.ReadSingle();
                intensity = reader.ReadSingle();
                radius = reader.ReadSingle();
                innerAngle = reader.ReadSingle();
                outerAngle = reader.ReadSingle();
                angleX = reader.ReadSingle();
                angleY = reader.ReadSingle();
                angleZ = reader.ReadSingle();

                argb[0] = reader.ReadInt32();
                argb[1] = reader.ReadInt32();
                argb[2] = reader.ReadInt32();
                argb[3] = reader.ReadInt32();

                txtLightType = reader.ReadString();
                txtHeight = reader.ReadString();
                txtIntensity = reader.ReadString();
                txtRadius = reader.ReadString();
                txtInnerAngle = reader.ReadString();
                txtOuterAngle = reader.ReadString();
                txtAngleX = reader.ReadString();
                txtAngleY = reader.ReadString();
                txtAngleZ = reader.ReadString();
                txtColor = reader.ReadString();

                if (Util.isEndOfStream(reader)) return; // Version handler

                // 1.0.1
                numFpOffset = reader.ReadSingle();
                txtFpOffset = reader.ReadString();

                if (Util.isEndOfStream(reader)) return;

                numSpecular = reader.ReadSingle();
                txtSpecular = reader.ReadString();


                // 1.0.2
                if (Util.isEndOfStream(reader)) return;
                txtStaticToVars = reader.ReadString();

            }

            public void save(BinaryWriter writer)
            {
                writer.Write(disablePlugin);
                writer.Write(settingMethod);
                writer.Write(usingFpView);
                writer.Write(cleanLights);
                writer.Write(disableSwitch);
                writer.Write(settingsToUse);

                writer.Write(lightType);
                writer.Write(height);
                writer.Write(intensity);
                writer.Write(radius);
                writer.Write(innerAngle);
                writer.Write(outerAngle);
                writer.Write(angleX);
                writer.Write(angleY);
                writer.Write(angleZ);

                for (int i = 0; i < argb.Length; i++)
                {
                    writer.Write(argb[i]);
                }

                writer.Write(txtLightType);
                writer.Write(txtHeight);
                writer.Write(txtIntensity);
                writer.Write(txtRadius);
                writer.Write(txtInnerAngle);
                writer.Write(txtOuterAngle);
                writer.Write(txtAngleX);
                writer.Write(txtAngleY);
                writer.Write(txtAngleZ);
                writer.Write(txtColor);

                // 1.0.1
                writer.Write(numFpOffset);
                writer.Write(txtFpOffset);

                writer.Write(numSpecular);
                writer.Write(txtSpecular);

                // 1.0.2

                writer.Write(txtStaticToVars);
            }
        }

    }

}
