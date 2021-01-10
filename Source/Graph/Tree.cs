// Tree.cs
// Copyright Karel Kroeze, 2020-2020

//using Multiplayer.API;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using static ResearchPal.Constants;

namespace ResearchPal
{
    public static class Tree
    {
        public static volatile bool Initialized = false;
        public static  IntVec2                         Size = IntVec2.Zero;
        public static bool shouldSeparateByTechLevels;

        private static List<Node>                      _nodes;
        private static List<Edge<Node, Node>>          _edges;
        private static List<TechLevel>                 _relevantTechLevels;
        private static Dictionary<TechLevel, IntRange> _techLevelBounds;

        public static bool OrderDirty;

        private static List<List<Node>> _layers;
        private static List<Node> _singletons;

        private static List<ResearchNode> _researchNodes;
        public static bool DisplayProgressState = false;

        public static Dictionary<TechLevel, IntRange> TechLevelBounds
        {
            get
            {
                if ( _techLevelBounds == null )
                    throw new Exception( "TechLevelBounds called before they are set." );
                return _techLevelBounds;
            }
        }

        public static List<TechLevel> RelevantTechLevels
        {
            get
            {
                if ( _relevantTechLevels == null )
                    _relevantTechLevels = Enum.GetValues( typeof( TechLevel ) )
                                              .Cast<TechLevel>()
                                               // filter down to relevant tech levels only.
                                              .Where(
                                                   tl => DefDatabase<ResearchProjectDef>.AllDefsListForReading.Any(
                                                       rp => rp.techLevel ==
                                                             tl ) )
                                              .ToList();
                return _relevantTechLevels;
            }
        }

        public static List<Node> Nodes
        {
            get
            {
                if ( _nodes == null )
                    PopulateNodes();

                return _nodes;
            }
        }

        public static List<ResearchNode> ResearchNodes() {
            if (_researchNodes == null || _researchNodes.Count() == 0) {
                _researchNodes = Nodes.OfType<ResearchNode>().ToList();
            }
            return _researchNodes;
        }

        public static IEnumerable<Node> NonSingletons => Nodes.Where(n => _singletons.IndexOf(n) == -1);

        public static List<Edge<Node, Node>> Edges
        {
            get
            {
                if ( _edges == null )
                    throw new Exception( "Trying to access edges before they are initialized." );

                return _edges;
            }
        }

        private static List<List<Node>> Layering(List<Node> nodes) {
            var layers = new List<List<Node>>();
            foreach (var node in Nodes)
            {
                if (node.X > layers.Count()) {
                    for (int i = layers.Count(); i < node.X; ++i) {
                        layers.Add(new List<Node>());
                    }
                }
                layers[node.X - 1].Add(node);
            }
            return layers;
        }

        private static bool SignDiff(float f1, float f2) {
            return f1 < 0 && f2 > 0 || f1 > 0 && f2 < 0;
        }

        private static float mainGraphUpperbound = 1;

        private static List<Node> ProcessSingletons(List<List<Node>> layers) {
            if (shouldSeparateByTechLevels) {
                return new List<Node>();
            }
            var singletons = layers[0]
                .Where(n => n is ResearchNode && n.OutEdges.Count() == 0)
                .OrderBy(n => (n as ResearchNode).Research.techLevel)
                .ToList();
            layers[0] = layers[0].Where(n => n.OutEdges.Count() > 0).ToList();

            foreach (var g in singletons.GroupBy(n => (n as ResearchNode).Research.techLevel)) {
                PlaceSingletons(g, layers.Count() - 1);
            }

            return singletons;
        }

        private static void PlaceSingletons(IEnumerable<Node> singletons, int colNum) {
            int x = 0, y = (int) mainGraphUpperbound;
            foreach (var n in singletons) {
                n.X = x + 1; n.Y = y;
                y += (x + 1) / colNum;
                x = (x + 1) % colNum;
            }
            mainGraphUpperbound = x == 0 ? y : y + 1;
        }

        private static void MergeDummiesByParents(List<Node> layer, List<DummyNode> dummies) {
            for (int i = 0; i < dummies.Count() - 1; ) {
                DummyNode node = dummies[i];
                List<Node> parents = node.InNodes;
                parents.Sort();
                int j = i + 1;
                for (; j < dummies.Count(); ++j) {
                    var parents2 = dummies[j].InNodes;
                    parents2.Sort();
                    if (!parents.SequenceEqual(parents2)) break;
                    node.Merge(dummies[j]);
                    Nodes.Remove(dummies[j]);
                    layer.Remove(dummies[j]);
                }
                i = j;
            }
        }
        private static void MergeDummiesByChildren(List<Node> layer, List<DummyNode> dummies) {
            for (int i = 0; i < dummies.Count() - 1; ) {
                DummyNode node = dummies[i];
                List<Node> children = node.OutNodes;
                children.SortBy(n => n.GetHashCode());
                int j = i + 1;
                for (; j < dummies.Count(); ++j) {
                    var children2 = dummies[j].OutNodes;
                    children2.SortBy(n => n.GetHashCode());
                    if (!children.SequenceEqual(children2)) break;
                    node.Merge(dummies[j]);
                    Nodes.Remove(dummies[j]);
                    layer.Remove(dummies[j]);
                }
                i = j;
            }
        }

        private static void CollapseAdjacentDummyNodes() {
            for (int i = 0; i < _layers.Count(); ++i) {
                var dummies = _layers[i].OfType<DummyNode>().ToList();
                MergeDummiesByParents(_layers[i], dummies);
            }
            for (int i = _layers.Count() - 1; i >= 0; --i) {
                var dummies = _layers[i].OfType<DummyNode>().ToList();
                MergeDummiesByChildren(_layers[i], dummies);
            }
        }

        public static void LegacyPreprocessing() {
            var layers = Layering(Nodes);
            var singletons = ProcessSingletons(layers);
            _layers = layers;
            _singletons = singletons;
        }

        public static void MainAlgorithm(List<List<Node>> data) {
            NodeLayers layers = new NodeLayers(data);
            // var layerss = new List<NodeLayers>();
            // layerss.Add(layers);
            List<NodeLayers> modsSplit = null;
            if (Settings.placeModTechSeparately) {
                modsSplit = layers.SplitLargeMods();
            } else {
                modsSplit = new List<NodeLayers>();
                modsSplit.Add(layers);
            }

            var allLayers = modsSplit
                .OrderBy(l => l.NodeCount())
                .SelectMany(
                    ls => ls
                        .SplitConnectiveComponents()
                        .OrderBy(l => l.NodeCount()))
                .ToList();
            allLayers.ForEach(l => OrgainzeLayers(l));
            PositionAllLayers(allLayers);
        }

        public static void OrgainzeLayers(NodeLayers layers) {
            layers.MinimizeCrossings();
            layers.ApplyGridCoordinates();
            layers.ImproveNodePositionsInLayers();
        }

        private static void FitLayersInBounds(NodeLayers layers, float[] topBounds) {
            float dy = -99999;
            for (int i = 0; i < layers.LayerCount(); ++i) {
                dy = Math.Max(dy, topBounds[i] - layers.TopPosition(i));
            }
            layers.MoveVertically(dy);
            for (int i = 0; i < layers.LayerCount(); ++i) {
                topBounds[i] = Math.Max(topBounds[i], layers.BottomPosition(i) + 1);
            }
        }

        public static void PositionAllLayers(IEnumerable<NodeLayers> layerss) {
            float[] topBounds = new float[_layers.Count()];
            for (int i = 0; i < topBounds.Count(); ++i) {
                topBounds[i] = mainGraphUpperbound;
            }
            foreach (var layers in layerss) {
                FitLayersInBounds(layers, topBounds);
            }
            mainGraphUpperbound = topBounds.Max();
        }


        public static void Initialize()
        {
            shouldSeparateByTechLevels = Settings.shouldSeparateByTechLevels;

            // setup
            // Log.Message(ResourceBank.String.PreparingTree_Setup);
            CheckPrerequisites();
            CreateEdges();
            HorizontalPositions();
            NormalizeEdges();
// Legacy Logic Above

            LegacyPreprocessing();
            MainAlgorithm(_layers);

            // CollapseAdjacentDummyNodes();

            RemoveEmptyRows();
            Tree.Size.z = (int) (Nodes.Max(n => n.Yf) + 0.01) + 1;

            Log.Message("Research layout initialized");
            Initialized = true;
// Legacy Logic Below

// #if DEBUG
//             DebugStatus();
// #endif
//             // crossing reduction
//             Log.Message(ResourceBank.String.PreparingTree_CrossingReduction);
//             Collapse();
//             MinimizeCrossings();
// #if DEBUG
//             DebugStatus();
// #endif
//             // layout
//             Log.Message(ResourceBank.String.PreparingTree_Layout);
//             MinimizeEdgeLength();
//             SquashOrphans();
            // RemoveEmptyRows();
// #if DEBUG
//             DebugStatus();
// #endif
//             // done!
//             // we're ready
//             Log.Message(ResourceBank.String.PreparingTree_RestoreQueue);
        }

        private static void RemoveEmptyRows()
        {
            Log.Debug( "Removing empty rows" );
            Profiler.Start();
            var z = Nodes.Max(n => n.Yf);
            for (var y = 1; y < z;) {
                var row = Row( y );
                if ( row.NullOrEmpty() ) {
                    var ns = Nodes.Where(n => n.Yf > y).ToList();
                    if (ns.Count() == 0) {
                        break;
                    }
                    ns.ForEach(n => n.Yf = n.Yf - 1);
                }
                else
                    ++y;
            }

            Profiler.End();
        }

        static void SquashOrphans() {
            if (!shouldSeparateByTechLevels) {
                var nodes = Nodes.OfType<ResearchNode>().GroupBy(n => n.Edges.Any());


                // get the min Y and max X from non orphans
                var nonOrphans = nodes.FirstOrDefault(g => g.Key);
                int minY = 0, maxX = 0;
                foreach (var node in nonOrphans) {
                    minY = Math.Min(node.Y, minY);
                    maxX = Math.Max(node.X, maxX);
                }

                // orphans ordered by tech level
                var orphans = nodes.FirstOrDefault(g => !g.Key).OrderBy(n => n.Research.techLevel);

                // take into account the total non orphan layers and create as many rows required
                int count = orphans.Count();
                int rows = (count + maxX - 1) / maxX;
                int index = 0;
                foreach (var node in orphans) {
                    node.SetDepth((index / rows) + 1);
                    node.Y = (index % rows) + 1;

                    index++;
                    Log.Debug("\t{0}", node);
                }

                // push the non orphans down in Y if there is overlap
                int overlap = rows - minY;
                if (overlap > 0) {
                    foreach (var node in nonOrphans)
                    {
                        node.Y += overlap + 1;
                    }
                }
            }
        }

        private static void MinimizeEdgeLength()
        {
            Log.Debug( "Minimize edge length." );
            Profiler.Start();

            // move and/or swap nodes to reduce the total edge length
            // perform sweeps of adjacent node reorderings
            var progress  = false;
            int iteration = 0, burnout = 2, max_iterations = 50;
            while ( ( !progress || burnout > 0 ) && iteration < max_iterations )
            {
                progress = EdgeLengthSweep_Local( iteration++ );
                if ( !progress )
                    burnout--;
            }

            // sweep until we had no progress 2 times, then keep sweeping until we had progress
            iteration = 0;
            burnout   = 2;
            while ( burnout > 0 && iteration < max_iterations )
            {
                progress = EdgeLengthSweep_Global( iteration++ );
                if ( !progress )
                    burnout--;
            }

            Profiler.End();
        }

        private static bool EdgeLengthSweep_Global( int iteration )
        {
            Profiler.Start( "iteration" + iteration );
            // calculate edge length before sweep
            var before = EdgeLength();

            // do left/right sweep, align with left/right nodes for 4 different iterations.
            //if (iteration % 2 == 0)
            for ( var l = 2; l <= Size.x; l++ )
                EdgeLengthSweep_Global_Layer( l, true );
            //else
            //    for (var l = 1; l < Size.x; l++)
            //        EdgeLengthSweep_Global_Layer(l, false);

            // calculate edge length after sweep
            var after = EdgeLength();

            // return progress
            Log.Debug( $"EdgeLengthSweep_Global, iteration {iteration}: {before} -> {after}" );
            Profiler.End();
            return after < before;
        }


        private static bool EdgeLengthSweep_Local( int iteration )
        {
            Profiler.Start( "iteration" + iteration );
            // calculate edge length before sweep
            var before = EdgeLength();

            // do left/right sweep, align with left/right nodes for 4 different iterations.
            if ( iteration % 2 == 0 )
                for ( var l = 2; l <= Size.x; l++ )
                    EdgeLengthSweep_Local_Layer( l, true );
            else
                for ( var l = Size.x - 1; l >= 0; l-- )
                    EdgeLengthSweep_Local_Layer( l, false );

            // calculate edge length after sweep
            var after = EdgeLength();

            // return progress
            Log.Debug( $"EdgeLengthSweep_Local, iteration {iteration}: {before} -> {after}" );
            Profiler.End();
            return after < before;
        }

        private static void EdgeLengthSweep_Global_Layer( int l, bool @in )
        {
            // The objective here is to;
            // (1) move and/or swap nodes to reduce total edge length
            // (2) not increase the number of crossings

            var length    = EdgeLength( l, @in );
            var crossings = Crossings( l );
            if ( Math.Abs( length ) < Epsilon )
                return;

            var layer = Layer( l, true );
            foreach ( var node in layer )
            {
                // we only need to loop over positions that might be better for this node.
                // min = minimum of current position, minimum of any connected nodes current position
                var neighbours = node.Nodes;
                if ( !neighbours.Any() )
                    continue;

                var min = Mathf.Min( node.Y, neighbours.Min( n => n.Y ) );
                var max = Mathf.Max( node.Y, neighbours.Max( n => n.Y ) );
                if ( min == max && min == node.Y )
                    continue;

                for ( var y = min; y <= max; y++ )
                {
                    if ( y == node.Y )
                        continue;

                    // is this spot occupied? 
                    var otherNode = NodeAt( l, y );

                    // occupied, try swapping
                    if ( otherNode != null )
                    {
                        Swap( node, otherNode );
                        var candidateCrossings = Crossings( l );
                        if ( candidateCrossings > crossings )
                        {
                            // abort
                            Swap( otherNode, node );
                        }
                        else
                        {
                            var candidateLength = EdgeLength( l, @in );
                            if ( length - candidateLength < Epsilon )
                            {
                                // abort
                                Swap( otherNode, node );
                            }
                            else
                            {
                                Log.Trace( "\tSwapping {0} and {1}: {2} -> {3}", node, otherNode, length,
                                           candidateLength );
                                length = candidateLength;
                            }
                        }
                    }

                    // not occupied, try moving
                    else
                    {
                        var oldY = node.Y;
                        node.Y = y;
                        var candidateCrossings = Crossings( l );
                        if ( candidateCrossings > crossings )
                        {
                            // abort
                            node.Y = oldY;
                        }
                        else
                        {
                            var candidateLength = EdgeLength( l, @in );
                            if ( length - candidateLength < Epsilon )
                            {
                                // abort
                                node.Y = oldY;
                            }
                            else
                            {
                                Log.Trace( "\tMoving {0} -> {1}: {2} -> {3}", node, new Vector2( node.X, oldY ), length,
                                           candidateLength );
                                length = candidateLength;
                            }
                        }
                    }
                }
            }
        }


        private static void EdgeLengthSweep_Local_Layer( int l, bool @in )
        {
            // The objective here is to;
            // (1) move and/or swap nodes to reduce local edge length
            // (2) not increase the number of crossings
            var x         = @in ? l - 1 : l + 1;
            var crossings = Crossings( x );

            var layer = Layer( l, true );
            foreach ( var node in layer )
            {
                foreach ( var edge in @in ? node.InEdges : node.OutEdges )
                {
                    // current length
                    var length    = edge.Length;
                    var neighbour = @in ? edge.In : edge.Out;
                    if ( neighbour.X != x )
                        Log.Warning( "{0} is not at layer {1}", neighbour, x );

                    // we only need to loop over positions that might be better for this node.
                    // min = minimum of current position, node position
                    var min = Mathf.Min( node.Y, neighbour.Y );
                    var max = Mathf.Max( node.Y, neighbour.Y );

                    // already at only possible position
                    if ( min == max && min == node.Y )
                        continue;

                    for ( var y = min; y <= max; y++ )
                    {
                        if ( y == neighbour.Y )
                            continue;

                        // is this spot occupied? 
                        var otherNode = NodeAt( x, y );

                        // occupied, try swapping
                        if ( otherNode != null )
                        {
                            Swap( neighbour, otherNode );
                            var candidateCrossings = Crossings( x );
                            if ( candidateCrossings > crossings )
                            {
                                // abort
                                Swap( otherNode, neighbour );
                            }
                            else
                            {
                                var candidateLength = edge.Length;
                                if ( length - candidateLength < Epsilon )
                                {
                                    // abort
                                    Swap( otherNode, neighbour );
                                }
                                else
                                {
                                    Log.Trace( "\tSwapping {0} and {1}: {2} -> {3}", neighbour, otherNode, length,
                                               candidateLength );
                                    length = candidateLength;
                                }
                            }
                        }

                        // not occupied, try moving
                        else
                        {
                            var oldY = neighbour.Y;
                            neighbour.Y = y;
                            var candidateCrossings = Crossings( x );
                            if ( candidateCrossings > crossings )
                            {
                                // abort
                                neighbour.Y = oldY;
                            }
                            else
                            {
                                var candidateLength = edge.Length;
                                if ( length - candidateLength < Epsilon )
                                {
                                    // abort
                                    neighbour.Y = oldY;
                                }
                                else
                                {
                                    Log.Trace( "\tMoving {0} -> {1}: {2} -> {3}", neighbour,
                                               new Vector2( neighbour.X, oldY ), length, candidateLength );
                                    length = candidateLength;
                                }
                            }
                        }
                    }
                }
            }
        }

        static void HorizontalPositions() {
            Log.Debug("Assigning horizontal positions.");
            Profiler.Start();

            if (shouldSeparateByTechLevels) {
                HorizontalPositionsByTechLevels();
            } else {
                HorizontalPositionsByDensity();
            }

            Profiler.End();
        }

        static void HorizontalPositionsByTechLevels()
        {
            // get list of techlevels
            var  techlevels = RelevantTechLevels;
            bool anyChange;
            var  iteration     = 1;
            var  maxIterations = 50;

            // assign horizontal positions based on tech levels and prerequisites
            do
            {
                Profiler.Start( "iteration " + iteration );
                var min = 1;
                anyChange = false;

                foreach ( var techlevel in techlevels )
                {
                    // enforce minimum x position based on techlevels
                    var nodes = Nodes.OfType<ResearchNode>().Where( n => n.Research.techLevel == techlevel );
                    if ( !nodes.Any() )
                        continue;

                    foreach ( var node in nodes )
                        anyChange = node.SetDepth( min ) || anyChange;

                    min = nodes.Max( n => n.X ) + 1;

                    Log.Trace( "\t{0}, change: {1}", techlevel, anyChange );
                }

                Profiler.End();
            } while ( anyChange && iteration++ < maxIterations );


            // store tech level boundaries
            _techLevelBounds = new Dictionary<TechLevel, IntRange>();
            foreach ( var techlevel in techlevels )
            {
                var nodes = Nodes.OfType<ResearchNode>().Where( n => n.Research.techLevel == techlevel );
                _techLevelBounds[techlevel] = new IntRange( nodes.Min( n => n.X ) - 1, nodes.Max( n => n.X ) );
            }
        }

        static void HorizontalPositionsByDensity() {
            foreach (var node in Nodes)
            {
                List<Node> level = new List<Node>();
                level.Add(node);

                int depth = 1;
                while (level.Count > 0 && level.Any(n => n.InNodes.Count > 0))
                {
                    // has any parent, increment level.
                    depth++;

                    // set level to next batch of distinct Parents, where Parents may not be itself.
                    level = level.SelectMany(n => n.InNodes).Distinct().Where(n => n != node).ToList();

                    // stop infinite recursion with loops of size greater than 2
                    if (depth > 100)
                    {
                        Log.Error("{0} has more than 100 levels of prerequisites. Is the Research Tree defined as a loop?", false, node);
                        break;
                    }
                }
                node.SetDepth(depth);
            }

        }

        private static void NormalizeEdges()
        {
            Log.Debug( "Normalizing edges." );
            Profiler.Start();
            foreach (var edge in new List<Edge<Node, Node>>(Edges.Where(e => e.Span > 1)))
            {
                Log.Trace( "\tCreating dummy chain for {0}", edge );

                // remove and decouple long edge
                Edges.Remove( edge );
                edge.In.OutEdges.Remove( edge );
                edge.Out.InEdges.Remove( edge );
                var cur     = edge.In;
                var yOffset = ( edge.Out.Yf - edge.In.Yf ) / edge.Span;

                // create and hook up dummy chain
                for ( var x = edge.In.X + 1; x < edge.Out.X; x++ )
                {
                    var dummy = new DummyNode();
                    dummy.X  = x;
                    dummy.Yf = edge.In.Yf + yOffset * ( x - edge.In.X );
                    var dummyEdge = new Edge<Node, Node>( cur, dummy );
                    cur.OutEdges.Add( dummyEdge );
                    dummy.InEdges.Add( dummyEdge );
                    _nodes.Add( dummy );
                    Edges.Add( dummyEdge );
                    cur = dummy;
                    Log.Trace( "\t\tCreated dummy {0}", dummy );
                }

                // hook up final dummy to out node
                var finalEdge = new Edge<Node, Node>( cur, edge.Out );
                cur.OutEdges.Add( finalEdge );
                edge.Out.InEdges.Add( finalEdge );
                Edges.Add( finalEdge );
            }

            Profiler.End();
        }
        private static void CreateEdges()
        {
            Log.Debug( "Creating edges." );
            Profiler.Start();
            // create links between nodes
            if ( _edges.NullOrEmpty() ) _edges = new List<Edge<Node, Node>>();

            foreach ( var node in Nodes.OfType<ResearchNode>() )
            {
                if ( node.Research.prerequisites.NullOrEmpty() )
                    continue;
                foreach ( var prerequisite in node.Research.prerequisites )
                {
                    ResearchNode prerequisiteNode = prerequisite;
                    if ( prerequisiteNode == null )
                        continue;
                    var edge = new Edge<Node, Node>( prerequisiteNode, node );
                    Edges.Add( edge );
                    node.InEdges.Add( edge );
                    prerequisiteNode.OutEdges.Add( edge );
                    Log.Trace( "\tCreated edge {0}", edge );
                }
            }

            Profiler.End();
        }

        private static void CheckPrerequisites()
        {
            // check prerequisites
            Log.Debug( "Checking prerequisites." );
            Profiler.Start();

            var nodes = new Queue<ResearchNode>( Nodes.OfType<ResearchNode>() );
            // remove redundant prerequisites
            while ( nodes.Count > 0 )
            {
                var node = nodes.Dequeue();
                if ( node.Research.prerequisites.NullOrEmpty() )
                    continue;

                var ancestors = node.Research.prerequisites?.SelectMany( r => r.Ancestors() ).ToList();
                var redundant = ancestors.Intersect( node.Research.prerequisites );
                if ( redundant.Any() )
                {
                    // Log.Warning( "\tredundant prerequisites for {0}: {1}", node.Research.LabelCap,
                                //  string.Join( ", ", redundant.Select( r => r.LabelCap ).ToArray() ) );
                    foreach ( var redundantPrerequisite in redundant )
                        node.Research.prerequisites.Remove( redundantPrerequisite );
                }
            }

            // fix bad techlevels
            nodes = new Queue<ResearchNode>( Nodes.OfType<ResearchNode>() );
            while ( nodes.Count > 0 )
            {
                var node = nodes.Dequeue();
                if ( !node.Research.prerequisites.NullOrEmpty() )
                    // warn and fix badly configured techlevels
                    if ( node.Research.prerequisites.Any( r => r.techLevel > node.Research.techLevel ) )
                    {
                        Log.Warning( "\t{0} has a lower techlevel than (one of) it's prerequisites",
                                     node.Research.label );
                        node.Research.techLevel = node.Research.prerequisites.Max( r => r.techLevel );

                        // re-enqeue all descendants
                        foreach ( var descendant in node.Descendants.OfType<ResearchNode>() )
                            nodes.Enqueue( descendant );
                    }
            }

            Profiler.End();
        }

        private static void PopulateNodes()
        {
            Log.Debug( "Populating nodes." );
            Profiler.Start();

            var projects = DefDatabase<ResearchProjectDef>.AllDefsListForReading;

            // find hidden nodes (nodes that have themselves as a prerequisite)
            var hidden = projects.Where( p => p.prerequisites?.Contains( p ) ?? false );

            // find locked nodes (nodes that have a hidden node as a prerequisite)
            var locked = projects.Where( p => p.Ancestors().Intersect( hidden ).Any() );

            // populate all nodes
            _nodes = new List<Node>( DefDatabase<ResearchProjectDef>.AllDefsListForReading
                                                                    .Except( hidden )
                                                                    .Except( locked )
                                                                    .Select( def => new ResearchNode( def ) as Node ) );
            Log.Debug( "\t{0} nodes", _nodes.Count );
            Profiler.End();
        }

        private static void Collapse()
        {
            Log.Debug( "Collapsing nodes." );
            Profiler.Start();
            var pre = Size;
            for ( var l = 1; l <= Size.x; l++ )
            {
                var nodes = Layer( l, true );
                var Y     = 1;
                foreach ( var node in nodes )
                    node.Y = Y++;
            }

            Log.Debug( "{0} -> {1}", pre, Size );
            Profiler.End();
        }

        [Conditional( "DEBUG" )]
        internal static void DebugDraw()
        {
            foreach ( var v in Nodes )
            {
                foreach ( var w in v.OutNodes ) Widgets.DrawLine( v.Right, w.Left, Color.white, 1 );
            }
        }

        // Invariant: currentHighlights should always contain highlightCauser
        static List<ResearchNode> currentHighlights;
        static ResearchNode highlightCauser;

        static List<ResearchNode> FindHighlightsFrom(ResearchNode node) {
            return node.GetMissingRequiredRecursive()
                .Concat(node.Children.Where(c => !c.Completed))
                .Append(node)
                .ToList();
        }

        static void StopCurrentHighlight() {
            highlightCauser = null;
            if (currentHighlights != null) {
                currentHighlights.ForEach(n => {
                    n.Highlighted(false);
                    n.mouseHoverHighlight = false;
                });
                currentHighlights = null;
            }
        }

        static void DoHighlight(ResearchNode node) {
            StopCurrentHighlight();

            highlightCauser = node;
            currentHighlights = FindHighlightsFrom(node);
            currentHighlights.ForEach(n => {
                n.Highlighted(true);
                n.mouseHoverHighlight = true;
            });
        }

        static void HandleHighlights(ResearchNode node, Vector2 mousePos) {
            if (! node.ShouldHighlight(mousePos) && highlightCauser == node) {
                StopCurrentHighlight();
            }
            if (node.ShouldHighlight(mousePos) && highlightCauser != node) {
                DoHighlight(node);
            }
        }

        public static void Draw( Rect visibleRect )
        {
            Profiler.Start( "Tree.Draw" );
            if (shouldSeparateByTechLevels)
            {
                Profiler.Start("techlevels");
                foreach (var techlevel in RelevantTechLevels)
                    DrawTechLevel(techlevel, visibleRect);
                Profiler.End();
            }

            Profiler.Start( "edges" );
            foreach ( var edge in Edges.OrderBy( e => e.DrawOrder ) )
                edge.Draw( visibleRect );
            Profiler.End();

            TryModifySharedState();

            Profiler.Start( "nodes" );
            var evt = new Event(Event.current);
            var drawnNodes = ResearchNodes().Where(n => n.IsVisible(visibleRect));
            foreach (var node in drawnNodes) {
                HandleHighlights(node, evt.mousePosition);
                node.Draw(visibleRect, 0, false);
            }
            Profiler.End();
        }
        private static void TryModifySharedState() {
            if (Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift)) {
                DisplayProgressState = true;
            } else if (Input.GetKeyUp(KeyCode.LeftShift) || Input.GetKeyUp(KeyCode.RightShift)) {
                DisplayProgressState = false;
            }
            // if (Event.current.type == EventType.KeyDown) {
            //     if (Event.current.keyCode == KeyCode.LeftShift || Event.current.keyCode == KeyCode.RightShift) {
            //         _displayProgressState = true;
            //     }
            // } else if (Event.current.type == EventType.KeyUp) {
            //     if (Event.current.keyCode == KeyCode.LeftShift || Event.current.keyCode == KeyCode.RightShift) {
            //         _displayProgressState = false;
            //     }
            // }
        }

        public static void DrawTechLevel( TechLevel techlevel, Rect visibleRect )
        {
            // determine positions
            var xMin = ( NodeSize.x + NodeMargins.x ) * TechLevelBounds[techlevel].min - NodeMargins.x / 2f;
            var xMax = ( NodeSize.x + NodeMargins.x ) * TechLevelBounds[techlevel].max - NodeMargins.x / 2f;

            GUI.color   = Assets.TechLevelColor;
            Text.Anchor = TextAnchor.MiddleCenter;

            // lower bound
            if ( TechLevelBounds[techlevel].min > 0 && xMin > visibleRect.xMin && xMin < visibleRect.xMax )
            {
                // line
                Widgets.DrawLine( new Vector2( xMin, visibleRect.yMin ), new Vector2( xMin, visibleRect.yMax ),
                                  Assets.TechLevelColor, 1f );

                // label
                var labelRect = new Rect(
                    xMin + TechLevelLabelSize.y / 2f - TechLevelLabelSize.x / 2f,
                    visibleRect.center.y             - TechLevelLabelSize.y / 2f,
                    TechLevelLabelSize.x,
                    TechLevelLabelSize.y );

                VerticalLabel( labelRect, techlevel.ToStringHuman() );
            }

            // upper bound
            if ( TechLevelBounds[techlevel].max < Size.x && xMax > visibleRect.xMin && xMax < visibleRect.xMax )
            {
                // label
                var labelRect = new Rect(
                    xMax - TechLevelLabelSize.y / 2f - TechLevelLabelSize.x / 2f,
                    visibleRect.center.y             - TechLevelLabelSize.y / 2f,
                    TechLevelLabelSize.x,
                    TechLevelLabelSize.y );

                VerticalLabel( labelRect, techlevel.ToStringHuman() );
            }

            GUI.color   = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private static void VerticalLabel( Rect rect, string text )
        {
            // store the scaling matrix
            var matrix = GUI.matrix;

            // rotate and then apply the scaling
            GUI.matrix = Matrix4x4.identity;
            GUIUtility.RotateAroundPivot( -90f, rect.center );
            GUI.matrix = matrix * GUI.matrix;

            Widgets.Label( rect, text );

            // restore the original scaling matrix
            GUI.matrix = matrix;
        }

        private static Node NodeAt( int X, int Y )
        {
            return Nodes.FirstOrDefault( n => n.X == X && n.Y == Y );
        }

        public static void MinimizeCrossings()
        {
            // initialize each layer by putting nodes with the most (recursive!) children on bottom
            Log.Debug( "Minimize crossings." );
            Profiler.Start();

            for ( var X = 1; X <= Size.x; X++ )
            {
                var nodes = Layer( X ).OrderBy( n => n.Descendants.Count ).ToList();
                for ( var i = 0; i < nodes.Count; i++ )
                    nodes[i].Y = i + 1;
            }

            // up-down sweeps of mean reordering
            var progress  = false;
            int iteration = 0, burnout = 2, max_iterations = 50;
            while ( ( !progress || burnout > 0 ) && iteration < max_iterations )
            {
                progress = BarymetricSweep( iteration++ );
                if ( !progress )
                    burnout--;
            }

            // greedy sweep for local optima
            iteration = 0;
            burnout   = 2;
            while ( burnout > 0 && iteration < max_iterations )
            {
                progress = GreedySweep( iteration++ );
                if ( !progress )
                    burnout--;
            }

            Profiler.End();
        }

        private static bool GreedySweep( int iteration )
        {
            Profiler.Start( "iteration " + iteration );

            // count number of crossings before sweep
            var before = Crossings();

            // do up/down sweep on aternating iterations
            if ( iteration % 2 == 0 )
                for ( var l = 1; l <= Size.x; l++ )
                    GreedySweep_Layer( l );
            else
                for ( var l = Size.x; l >= 1; l-- )
                    GreedySweep_Layer( l );

            // count number of crossings after sweep
            var after = Crossings();

            Log.Debug( $"GreedySweep: {before} -> {after}" );
            Profiler.End();

            // return progress
            return after < before;
        }

        private static void GreedySweep_Layer( int l )
        {
            // The objective here is twofold;
            // 1: Swap nodes to reduce the number of crossings
            // 2: Swap nodes so that inner edges (edges between dummies)
            //    avoid crossings at all costs.
            //
            // If I'm reasoning this out right, both objectives should be served by
            // minimizing the amount of crossings between each pair of nodes.
            var crossings = Crossings( l );
            if ( crossings == 0 )
                return;

            var layer = Layer( l, true );
            for ( var i = 0; i < layer.Count - 1; i++ )
            {
                for ( var j = i + 1; j < layer.Count; j++ )
                {
                    // swap, then count crossings again. If lower, leave it. If higher, revert.
                    Swap( layer[i], layer[j] );
                    var candidateCrossings = Crossings( l );
                    if ( candidateCrossings < crossings )
                        // update current crossings
                        crossings = candidateCrossings;
                    else
                        // revert change
                        Swap( layer[j], layer[i] );
                }
            }
        }

        private static void Swap( Node A, Node B )
        {
            if ( A.X != B.X )
                throw new Exception( "Can't swap nodes on different layers" );

            // swap Y positions of adjacent nodes
            var tmp = A.Y;
            A.Y = B.Y;
            B.Y = tmp;
        }

        private static bool BarymetricSweep( int iteration )
        {
            Profiler.Start( "iteration " + iteration );

            // count number of crossings before sweep
            var before = Crossings();

            // do up/down sweep on alternating iterations
            if ( iteration % 2 == 0 )
                for ( var i = 2; i <= Size.x; i++ )
                    BarymetricSweep_Layer( i, true );
            else
                for ( var i = Size.x - 1; i > 0; i-- )
                    BarymetricSweep_Layer( i, false );

            // count number of crossings after sweep
            var after = Crossings();

            // did we make progress? please?
            Log.Debug(
                $"BarymetricSweep {iteration} ({( iteration % 2 == 0 ? "left" : "right" )}): {before} -> {after}" );
            Profiler.End();
            return after < before;
        }

        private static void BarymetricSweep_Layer( int layer, bool left )
        {
            var means = Layer( layer )
                       .ToDictionary( n => n, n => GetBarycentre( n, left ? n.InNodes : n.OutNodes ) )
                       .OrderBy( n => n.Value );

            // create groups of nodes at similar means
            var cur    = float.MinValue;
            var groups = new Dictionary<float, List<Node>>();
            foreach ( var mean in means )
            {
                if ( Math.Abs( mean.Value - cur ) > Epsilon )
                {
                    cur         = mean.Value;
                    groups[cur] = new List<Node>();
                }

                groups[cur].Add( mean.Key );
            }

            // position nodes as close to their desired mean as possible
            var Y = 1;
            foreach ( var group in groups )
            {
                var mean = group.Key;
                var N    = group.Value.Count;
                Y = (int) Mathf.Max( Y, mean - ( N - 1 ) / 2 );

                foreach ( var node in group.Value )
                    node.Y = Y++;
            }
        }

        private static float GetBarycentre( Node node, List<Node> neighbours )
        {
            if ( neighbours.NullOrEmpty() )
                return node.Yf;

            return neighbours.Sum( n => n.Yf ) / neighbours.Count;
        }

        private static int Crossings()
        {
            var crossings                                            = 0;
            for ( var layer = 1; layer < Size.x; layer++ ) crossings += Crossings( layer, true );
            return crossings;
        }

        private static float EdgeLength()
        {
            var length                                            = 0f;
            for ( var layer = 1; layer < Size.x; layer++ ) length += EdgeLength( layer, true );
            return length;
        }

        private static int Crossings( int layer )
        {
            if ( layer == 0 )
                return Crossings( layer, false );
            if ( layer == Size.x )
                return Crossings( layer, true );
            return Crossings( layer, true ) + Crossings( layer, false );
        }

        private static float EdgeLength( int layer )
        {
            if ( layer == 0 )
                return EdgeLength( layer, false );
            if ( layer == Size.x )
                return EdgeLength( layer, true );
            return EdgeLength( layer, true ) *
                   EdgeLength( layer, false ); // multply to favor moving nodes closer to one endpoint
        }

        private static int Crossings( int layer, bool @in )
        {
            // get in/out edges for layer
            var edges = Layer( layer )
                       .SelectMany( n => @in ? n.InEdges : n.OutEdges )
                       .OrderBy( e => e.In.Y )
                       .ThenBy( e => e.Out.Y )
                       .ToList();

            if ( edges.Count < 2 )
                return 0;

            // count number of inversions
            var inversions = 0;
            for ( var i = 0; i < edges.Count - 1; i++ )
            {
                for ( var j = i + 1; j < edges.Count; j++ )
                    if ( edges[j].Out.Y < edges[i].Out.Y )
                        inversions++;
            }

            return inversions;
        }

        private static float EdgeLength( int layer, bool @in )
        {
            // get in/out edges for layer
            var edges = Layer( layer )
                       .SelectMany( n => @in ? n.InEdges : n.OutEdges )
                       .OrderBy( e => e.In.Y )
                       .ThenBy( e => e.Out.Y )
                       .ToList();

            if ( edges.NullOrEmpty() )
                return 0f;

            return edges.Sum( e => e.Length ) * ( @in ? 2 : 1 );
        }

        public static List<Node> Layer( int depth, bool ordered = false )
        {
            if ( ordered && OrderDirty )
            {
                _nodes     = Nodes.OrderBy( n => n.X ).ThenBy( n => n.Y ).ToList();
                OrderDirty = false;
            }

            return Nodes.Where( n => n.X == depth ).ToList();
        }

        public static List<Node> Row( int Y )
        {
            return Nodes.Where( n => n.Y == Y ).ToList();
        }

        public new static string ToString()
        {
            var text = new StringBuilder();

            for ( var l = 1; l <= Nodes.Max( n => n.X ); l++ )
            {
                text.AppendLine( $"Layer {l}:" );
                var layer = Layer( l, true );

                foreach ( var n in layer )
                {
                    text.AppendLine( $"\t{n}" );
                    text.AppendLine( "\t\tAbove: " +
                                     string.Join( ", ", n.InNodes.Select( a => a.ToString() ).ToArray() ) );
                    text.AppendLine( "\t\tBelow: " +
                                     string.Join( ", ", n.OutNodes.Select( b => b.ToString() ).ToArray() ) );
                }
            }

            return text.ToString();
        }

        public static void DebugStatus()
        {
            Log.Message( "duplicated positions:\n " +
                         string.Join(
                             "\n",
                             Nodes.Where( n => Nodes.Any( n2 => n != n2 && n.X == n2.X && n.Y == n2.Y ) )
                                  .Select( n => n.X + ", " + n.Y + ": " + n.Label ).ToArray() ) );
            Log.Message( "out-of-bounds nodes:\n" +
                         string.Join(
                             "\n", Nodes.Where( n => n.X < 1 || n.Y < 1 ).Select( n => n.ToString() ).ToArray() ) );
            Log.Trace( ToString() );
        }
    }
}