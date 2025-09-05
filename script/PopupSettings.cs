using System.IO;
using Yukar.Common;
using Yukar.Common.Rom;

namespace Bakin
{
    internal class PopupSettings : IChunk
    {
        public bool disablePlugin = false;

        public bool useAddMinus = true;
        public bool useItemIcon = true;
        public bool useSound = true;
        public bool usePopupColor = false;
        public bool useBackground = true;

        public int textPositionX;
        public int textPositionY;
        public float fontSize;

        public int iconPositionX;
        public int iconPositionY;
        public int iconSizeX;
        public int iconSizeY;

        public string customBackground = "default";
        public int backgroundPositionX;
        public int backgroundPositionY;
        public int backgroundSizeX;
        public int backgroundSizeY;

        public int popupOffset = 45;

        public string textColor = "255,255,255";
        public string addingColor = "144, 238, 144";
        public string removingColor = "205, 92, 92";

        public string customSound = "default";

        public int maxItemsToShow = 4;
        public void load(BinaryReader reader)
        {
            disablePlugin = reader.ReadBoolean();

            useAddMinus = reader.ReadBoolean();
            useItemIcon = reader.ReadBoolean();
            useSound = reader.ReadBoolean();
            usePopupColor = reader.ReadBoolean();
            useBackground = reader.ReadBoolean();

            textPositionX = reader.ReadInt32();
            textPositionY = reader.ReadInt32();
            fontSize = reader.ReadSingle();

            iconPositionX = reader.ReadInt32();
            iconPositionY = reader.ReadInt32();
            iconSizeX = reader.ReadInt32();
            iconSizeY = reader.ReadInt32();

            customBackground = reader.ReadString();
            backgroundPositionX = reader.ReadInt32();
            backgroundPositionY = reader.ReadInt32();
            backgroundSizeX = reader.ReadInt32();
            backgroundSizeY = reader.ReadInt32();

            popupOffset = reader.ReadInt32();

            textColor = reader.ReadString();
            addingColor = reader.ReadString();
            removingColor = reader.ReadString();

            customSound = reader.ReadString();

            maxItemsToShow = reader.ReadInt32();

            if (Util.isEndOfStream(reader)) return; // Version handler
        }

        public void save(BinaryWriter writer)
        {
            writer.Write(disablePlugin);
            writer.Write(useAddMinus);
            writer.Write(useItemIcon);
            writer.Write(useSound);
            writer.Write(usePopupColor);
            writer.Write(useBackground);

            writer.Write(textPositionX);
            writer.Write(textPositionY);
            writer.Write(fontSize);

            writer.Write(iconPositionX);
            writer.Write(iconPositionY);
            writer.Write(iconSizeX);
            writer.Write(iconSizeY);

            writer.Write(customBackground);
            writer.Write(backgroundPositionX);
            writer.Write(backgroundPositionY);
            writer.Write(backgroundSizeX);
            writer.Write(backgroundSizeY);

            writer.Write(popupOffset);

            writer.Write(textColor);
            writer.Write(addingColor);
            writer.Write(removingColor);

            writer.Write(customSound);

            writer.Write(maxItemsToShow);
        }
    }
}
