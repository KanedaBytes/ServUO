# Custom/Mobiles

Custom NPCs. Quest givers derive from `MondainQuester` and bind their quests with
`public override Type[] Quests => new[] { typeof(SomeQuest) };` — ServUO has no central quest
registry. See CLAUDE.md §11.
