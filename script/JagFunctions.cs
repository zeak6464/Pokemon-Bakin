using System;
using System.Collections.Generic;
using Yukar.Engine;

namespace Bakin
{
    public class JagFunctions : BakinObject
    {
        private bool isPaused;
        private List<Guid> layoutCacheList;
        private Dictionary<Guid, string> chrMotionDic;

        public override void Start()
        {
            // キャラクターが生成される時に、このメソッドがコールされます。
            // This method is called when the character is created.
        }

        public override void Update()
        {
            if (mapScene.mapCharList == null) return;

            if (!mapScene.menuWindow.isVisible() && !mapScene.menuWindow.isVisibleMainMenu())
            {
                if (isPaused)
                {
                    isPaused = false;
                 //   GameMain.sGameSpeed = 1f;

                    foreach (var layout in layoutCacheList)
                        mapScene.owner.ShowFreeLayout(layout);

                }
                return;
            }
            if (!isPaused)
            {

                layoutCacheList = new List<Guid>();
                isPaused = true;

                chrMotionDic = new Dictionary<Guid, string>();

                foreach (var chr in mapScene.mapCharList)
                {
                    if (chr.getGraphic() == null) continue;
                    if (chrMotionDic.ContainsKey(chr.guId))
                        chrMotionDic.Add(chr.guId, chr.currentMotion);
                    if (chr.currentMotion == "run")
                    {
                        chr.playMotion("wait", 0.2f);
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

                //   GameMain.sGameSpeed = 0.03f;
            }

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

        public override void Destroy()
        {
            // キャラクターが破棄される時に、このメソッドがコールされます。
            // This method is called when the character is destroyed.
        }

        public override void AfterDraw()
        {


            // このフレームの2D描画処理の最後に、このメソッドがコールされます。
            // This method is called at the end of the 2D drawing process for this frame.
        }

        [BakinFunction(Description = "Change party offset")]
        public void ChangePartyOffset(float offset)
        {


            // [BakinFunction] を付与したメソッドはイベントパネル「C#プログラムの呼び出し」からコールできます。int/float/string の戻り値および引数を一つまで取ることができます。
            // One of the methods with [BakinFunction] can be called from the event panel "Calling C# Programs".  Up to one int/float/string return value and parameter can be used.


        }
    }
}
