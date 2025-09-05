using Microsoft.Xna.Framework;
using Yukar.Common.GameData;
using Yukar.Common.Resource;
using Yukar.Engine;


namespace Bakin
{
    internal class ItemDrawer
    {
        private Party.ItemStack itemStack;
        private string symbol;
        private Color textColor;
        private Icon.Ref icon;
        private Texture iconTex;
        private float time;
        private bool soundPlayer;
        public Party.ItemStack ItemStack { get => itemStack; private set => itemStack = value; }
        public Icon.Ref Icon { get => icon; private set => icon = value; }
        public Texture IconTex { get => iconTex; private set => iconTex = value; }
        public float Time { get => time; set => time = value; }
        public string Symbol { get => symbol; private set => symbol = value; }
        public Color TextColor { get => textColor; private set => textColor = value; }
        public bool SoundPlayed { get => soundPlayer; set => soundPlayer = value; }

        public ItemDrawer(Party.ItemStack item, string symbol, Microsoft.Xna.Framework.Color textColor)
        {
            itemStack = item;
            this.symbol = symbol;
            this.textColor = textColor;
            IconInitialize();
        }

        private void IconInitialize()
        {
            var icon = ItemStack.item.icon;
            var iconTexture = GameMain.instance.catalog.getItemFromGuid<Texture>(icon.guId);

            Graphics.LoadImage(iconTexture);
            // iconTexture.getTexture().addRef();
            // Texture iconImgId = Graphics.GetIconImgId(iconTexture);

            this.Icon = icon;
            IconTex = iconTexture;
        }
    }

}
