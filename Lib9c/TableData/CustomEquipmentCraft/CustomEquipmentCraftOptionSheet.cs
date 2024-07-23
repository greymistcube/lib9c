using System;
using System.Collections.Generic;
using Nekoyume.Model.Item;
using static Nekoyume.TableData.TableExtensions;

namespace Nekoyume.TableData.CustomEquipmentCraft
{
    [Serializable]
    public class
        CustomEquipmentCraftOptionSheet : Sheet<ItemSubType, CustomEquipmentCraftOptionSheet.Row>
    {
        [Serializable]
        public class Row : SheetRow<ItemSubType>
        {
            public override ItemSubType Key => ItemSubType;
            public ItemSubType ItemSubType { get; private set; }
            public int HpRatio { get; private set; }
            public int AtkRatio { get; private set; }
            public int DefRatio { get; private set; }
            public int CriRatio { get; private set; }
            public int HitRatio { get; private set; }
            public int SpdRatio { get; private set; }
            public int DrvRatio { get; private set; }
            public int DrrRatio { get; private set; }

            public int CdmgRatio { get; private set; }

            // Armor Penetration, Not Action Point
            public int ApRatio { get; private set; }
            public int ThornRatio { get; private set; }

            public override void Set(IReadOnlyList<string> fields)
            {
                var val = 0;
                ItemSubType = (ItemSubType)Enum.Parse(typeof(ItemSubType), fields[0]);
                HpRatio = TryParseInt(fields[1], out val) ? val : 0;
                AtkRatio = TryParseInt(fields[2], out val) ? val : 0;
                DefRatio = TryParseInt(fields[3], out val) ? val : 0;
                CriRatio = TryParseInt(fields[4], out val) ? val : 0;
                HitRatio = TryParseInt(fields[5], out val) ? val : 0;
                SpdRatio = TryParseInt(fields[6], out val) ? val : 0;
                DrrRatio = TryParseInt(fields[7], out val) ? val : 0;
                DrvRatio = TryParseInt(fields[8], out val) ? val : 0;
                CdmgRatio = TryParseInt(fields[9], out val) ? val : 0;
                ApRatio = TryParseInt(fields[10], out val) ? val : 0;
                ThornRatio = TryParseInt(fields[11], out val) ? val : 0;
            }
        }

        public CustomEquipmentCraftOptionSheet(string name) : base(name)
        {
        }
    }
}
