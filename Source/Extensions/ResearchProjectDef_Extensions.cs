// ResearchProjectDef_Extensions.cs
// Copyright Karel Kroeze, 2019-2020

using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using Verse;

namespace ResearchPowl
{
    public static class ResearchProjectDef_Extensions
    {
        private static readonly Dictionary<Def, List<Def>> _unlocksCache =
            new Dictionary<Def, List<Def>>();

        public static List<ResearchProjectDef> Descendants( this ResearchProjectDef research )
        {
            var descendants = new HashSet<ResearchProjectDef>();

            // recursively go through all children
            // populate initial queue
            var queue = new Queue<ResearchProjectDef>(
                DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(
                    res => res.prerequisites?.Contains( research ) ?? false ) );

            // add to the list, and queue up children.
            while ( queue.Count > 0 )
            {
                var current = queue.Dequeue();
                descendants.Add( current );

                foreach ( var descendant in DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(
                    res =>
                        res.prerequisites?.Contains(
                            current ) ??
                        false && !descendants.Contains(
                            res ) ) )
                    queue.Enqueue( descendant );
            }

            return descendants.ToList();
        }

        public static IEnumerable<ThingDef> GetPlantsUnlocked( this ResearchProjectDef research )
        {
            return DefDatabase<ThingDef>.AllDefsListForReading
                                        .Where(
                                             td => td.plant?.sowResearchPrerequisites?.Contains( research ) ?? false );
        }

        public static List<ResearchProjectDef> Ancestors( this ResearchProjectDef research )
        {
            // keep a list of prerequites
            var prerequisites = new List<ResearchProjectDef>();
            if ( research.prerequisites.NullOrEmpty() )
                return prerequisites;

            // keep a stack of prerequisites that should be checked
            var stack = new Stack<ResearchProjectDef>(research.prerequisites.Where(parent => parent != research));

            // keep on checking everything on the stack until there is nothing left
            while (stack.Count > 0) {
                // add to list of prereqs
                var parent = stack.Pop();
                prerequisites.Add( parent );

                // add prerequitsite's prereqs to the stack
                if ( !parent.prerequisites.NullOrEmpty() )
                    foreach ( var grandparent in parent.prerequisites )
                        // but only if not a prerequisite of itself, and not a cyclic prerequisite
                        if ( grandparent != parent && !prerequisites.Contains( grandparent ) )
                            stack.Push( grandparent );
            }

            return prerequisites.Distinct().ToList();
        }

        public static IEnumerable<RecipeDef> GetRecipesUnlocked( this ResearchProjectDef research )
        {
            // recipe directly locked behind research
            var direct =
                DefDatabase<RecipeDef>.AllDefs.Where(rd =>
                    rd.researchPrerequisite == research ||
                    rd.researchPrerequisites != null && rd.researchPrerequisites.Contains(research));

            // recipe building locked behind research
            // var building = DefDatabase<ThingDef>.AllDefsListForReading
            //     .Where(
            //          td => ( td.researchPrerequisites?.Contains( research ) ?? false )
            //             && !td.AllRecipes.NullOrEmpty() )
            //     .SelectMany( td => td.AllRecipes )
            //     .Where( rd => rd.researchPrerequisite == null );

            // return union of these two sets
            return direct;
            // return direct.Concat( building ).Distinct();
        }

        public static IEnumerable<TerrainDef> GetTerrainUnlocked( this ResearchProjectDef research )
        {
            return DefDatabase<TerrainDef>.AllDefsListForReading
                                          .Where( td => td.researchPrerequisites?.Contains( research ) ?? false );
        }

        public static IEnumerable<ThingDef> GetThingsUnlocked( this ResearchProjectDef research )
        {
            return DefDatabase<ThingDef>.AllDefsListForReading
                                        .Where( td => td.researchPrerequisites?.Contains( research ) ?? false );
        }

        public static List<Def> GetUnlockDefs(this ResearchProjectDef research)
        {
            if ( _unlocksCache.ContainsKey( research ) )
                return _unlocksCache[research];

            var unlocks = new List<Def>();

            unlocks.AddRange(research.GetThingsUnlocked().Where(d => d.IconTexture() != null));

            unlocks.AddRange(research.GetTerrainUnlocked().Where(d => d.IconTexture() != null));

            unlocks.AddRange(research.GetRecipesUnlocked().Where(d => d.IconTexture() != null));

            unlocks.AddRange(research.GetPlantsUnlocked().Where(d => d.IconTexture() != null));

            // get unlocks for all descendant research, and remove duplicates.
            _unlocksCache.Add( research, unlocks );
            return unlocks;
        }

        public static ResearchNode ResearchNode( this ResearchProjectDef research )
        {
            var node = Tree.ResearchNodes().FirstOrDefault( n => n.Research == research );
            if ( node == null )
                Log.Error( "Node for {0} not found. Was it intentionally hidden or locked?", true, research.LabelCap );
            return node;
        }
    }
}