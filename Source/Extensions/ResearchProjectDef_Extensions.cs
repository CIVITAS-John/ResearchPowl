// ResearchProjectDef_Extensions.cs
// Copyright Karel Kroeze, 2019-2020

using System.Collections.Generic;
using System.Linq;
using Verse;

namespace ResearchPowl
{
    public static class ResearchProjectDef_Extensions
    {
        public static List<ResearchProjectDef> Ancestors(this ResearchProjectDef research)
        {
            // keep a list of prerequites
            var prerequisites = new List<ResearchProjectDef>();
            if ( research.prerequisites.NullOrEmpty() ) return prerequisites;

            // keep a stack of prerequisites that should be checked
            var stack = new Stack<ResearchProjectDef>();
            var list = research.prerequisites;
            for (int i = list.Count; i-- > 0;)
            {
                var def = list[i];
                if (def != research) stack.Push(def);
            }

            // keep on checking everything on the stack until there is nothing left
            while (stack.Count > 0)
            {
                // add to list of prereqs
                var parent = stack.Pop();
                prerequisites.Add(parent);

                // add prerequitsite's prereqs to the stack
                if (!parent.prerequisites.NullOrEmpty())
                {
                    for (int i = list.Count; i-- > 0;)
                    {
                        var grandparent = list[i];
                        // but only if not a prerequisite of itself, and not a cyclic prerequisite
                        if (grandparent != parent && !prerequisites.Contains(grandparent)) stack.Push(grandparent);
                    }
                }
            }

            return new List<ResearchProjectDef>(prerequisites.Distinct());
        }
        
        public static ResearchNode ResearchNode( this ResearchProjectDef research )
        {
            var node = Tree.ResearchNodes().FirstOrDefault( n => n.Research == research );
            if ( node == null )
                Log.Error( "Node for {0} not found. Was it intentionally hidden or locked?", true, research.LabelCap );
            return node;
        }

        // Returned for Human Resources to Patch
		public static readonly Dictionary<Def, List<ThingDef>> _thingsUnlocked = new Dictionary<Def, List<ThingDef>>();
		public static readonly Dictionary<Def, List<TerrainDef>> _terrainsUnlocked = new Dictionary<Def, List<TerrainDef>>();
		public static readonly Dictionary<Def, List<RecipeDef>> _recipesUnlocked = new Dictionary<Def, List<RecipeDef>>();
		public static readonly Dictionary<Def, List<ThingDef>> _plantsUnlocked = new Dictionary<Def, List<ThingDef>>();
		public static readonly Dictionary<Def, List<Def>> _unlocksCache = new Dictionary<Def, List<Def>>();

		public static List<ThingDef> GetThingsUnlocked(this ResearchProjectDef research) {
			if (!_thingsUnlocked.ContainsKey(research))
				GetUnlockDefs(research);
			return _thingsUnlocked[research];
		}

		public static List<TerrainDef> GetTerrainsUnlocked(this ResearchProjectDef research) {
			if (!_terrainsUnlocked.ContainsKey(research))
				GetUnlockDefs(research);
			return _terrainsUnlocked[research];
		}
		
		public static List<RecipeDef> GetRecipesUnlocked(this ResearchProjectDef research) {
			if (!_recipesUnlocked.ContainsKey(research))
				GetUnlockDefs(research);
			return _recipesUnlocked[research];
		}

		public static List<ThingDef> GetPlantsUnlocked(this ResearchProjectDef research) {
			if (!_plantsUnlocked.ContainsKey(research))
				GetUnlockDefs(research);
			return _plantsUnlocked[research];
		}

		public static List<Def> GetUnlockDefs(this ResearchProjectDef research) {
			if (_unlocksCache.ContainsKey(research))
				return _unlocksCache[research];

			var unlocks = new List<Def>();

			//Was GetThingsUnlocked()
			var things = new List<ThingDef>();
			var thingDefs = DefDatabase<ThingDef>.AllDefsListForReading;
			var length = thingDefs.Count;
			for (int i = 0; i < length; i++) {
				var def = thingDefs[i];
				if (def.researchPrerequisites?.Contains(research) ?? false && def.IconTexture() != null) {
					unlocks.Add(def);
					things.Add(def);
				}
			}
			_thingsUnlocked.Add(research, things);

			//Was GetTerrainUnlocked()
			var terrains = new List<TerrainDef>();
			var terrainDefs = DefDatabase<TerrainDef>.AllDefsListForReading;
			length = terrainDefs.Count;
			for (int i = 0; i < length; i++) {
				var def = terrainDefs[i];
				if (def.researchPrerequisites?.Contains(research) ?? false && def.IconTexture() != null) {
					unlocks.Add(def);
					terrains.Add(def);
				}
			}
			_terrainsUnlocked.Add(research, terrains);

			//Was GetRecipesUnlocked()
			var recipes = new List<RecipeDef>();
			var recipeDefs = DefDatabase<RecipeDef>.AllDefsListForReading;
			length = recipeDefs.Count;
			for (int i = 0; i < length; i++) {
				var def = recipeDefs[i];
				if ((def.researchPrerequisite == research || def.researchPrerequisites != null && def.researchPrerequisites.Contains(research)) &&
					def.IconTexture() != null) {
					unlocks.Add(def);
					recipes.Add(def);
				}
			}
			_recipesUnlocked.Add(research, recipes);

			// Was GetPlantsUnlocked()
			var plants = new List<ThingDef>();
			var plantDefs = DefDatabase<ThingDef>.AllDefsListForReading;
			length = plantDefs.Count;
			for (int i = 0; i < length; i++) {
				var def = plantDefs[i];
				if (def.plant?.sowResearchPrerequisites?.Contains(research) ?? false && def.IconTexture() != null) unlocks.Add(def);
			}
			_plantsUnlocked.Add(research, plants);
			
			// get unlocks for all descendant research, and remove duplicates.
			_unlocksCache.Add(research, unlocks);
			return unlocks;
		}
	}
}