using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yukar.Common.Rom;
using Yukar.Engine;

namespace Yukar.Battle
{
    class ReflectionInfo
    {
        public bool isFriendEffect;
        public BattleCharacterBase target;// 反射エフェクトを表示する対象 / To display reflective effects
        public BattleImpactReflectionPercentParam reflect;
        public int index = -1;// TargetCharacters の何番目と対応しているか？ / What number of TargetCharacters does it correspond to?

        public ReflectionInfo(bool isFriendEffect, BattleCharacterBase target, BattleImpactReflectionPercentParam reflect)
        {
            this.isFriendEffect = isFriendEffect;
            this.target = target;
            this.reflect = reflect;
        }
    }
}
