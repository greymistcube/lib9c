using Nekoyume.TableData;

namespace Nekoyume.Model.Skill
{
    public interface ISkill
    {
        public SkillSheet.Row SkillRow { get; }
        /// <summary>
        /// Determines damage of `AttackSkill`.
        /// Determines effect of `BuffSkill`.
        /// </summary>
        public int Power { get; }
        public int Chance { get; }
        public SkillCustomField? CustomField { get; }
        public void Update(int chance, int power);
    }
}
