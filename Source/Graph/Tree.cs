// Tree.cs
// Copyright Karel Kroeze, 2020-2020

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using static ResearchPowl.Constants;

namespace ResearchPowl
{
	public static class Tree
	{
		public static volatile bool Initialized, Initializing;
		public static IntVec2 Size = IntVec2.Zero;
		public static bool _shouldSeparateByTechLevels, DisplayProgressState, OrderDirty;
		static List<Node> _nodes, _singletons;
		static List<Edge<Node, Node>> _edges;
		static List<TechLevel> _relevantTechLevels;
		static Dictionary<TechLevel, IntRange> _techLevelBounds;
		static bool prerequisitesFixed;
		static List<List<Node>> _layers;
		static List<ResearchNode> _researchNodes;
		static float mainGraphUpperbound = 1;
		static RelatedNodeHighlightSet hoverHighlightSet;
		static List<RelatedNodeHighlightSet> fixedHighlightSets = new List<RelatedNodeHighlightSet>();

		public static Dictionary<TechLevel, IntRange> TechLevelBounds()
		{
			if ( _techLevelBounds == null ) throw new Exception( "TechLevelBounds called before they are set." );
			return _techLevelBounds;
		}
		public static List<TechLevel> RelevantTechLevels()
		{
			if (_relevantTechLevels != null) return _relevantTechLevels;

			_relevantTechLevels = new List<TechLevel>();
			List<ResearchProjectDef> sortedDefs = new List<ResearchProjectDef>(DefDatabase<ResearchProjectDef>.AllDefsListForReading.OrderBy(x => x.techLevel));
			
			var length = sortedDefs.Count;
			TechLevel lastTechlevel = 0;
			for (int i = 0; i < length; i++)
			{
				var def = sortedDefs[i];
				if (def.techLevel != lastTechlevel) _relevantTechLevels.Add(def.techLevel);
				lastTechlevel = def.techLevel;
			}

			return _relevantTechLevels;
		}
		public static List<Node> Nodes()
		{
			if ( _nodes == null ) InitializeNodesStructures();
			return _nodes;
		}
		public static List<ResearchNode> ResearchNodes()
		{
			if (_researchNodes == null) InitializeNodesStructures();
			return _researchNodes;
		}
		public static List<ResearchNode> WaitForResearchNodes()
		{
			while (_researchNodes == null) continue;
			return _researchNodes;
		}
		public static List<Edge<Node, Node>> Edges
		{
			get
			{
				if ( _edges == null )
					throw new Exception( "Trying to access edges before they are initialized." );

				return _edges;
			}
		}
		static List<List<Node>> Layering(List<Node> nodes) {
			var layers = new List<List<Node>>();
			foreach (var node in Nodes())
			{
				var nodeX = node.X;
				if (nodeX > layers.Count)
				{
					for (int i = layers.Count; i < nodeX; ++i)
					{
						layers.Add(new List<Node>());
					}
				}
				layers[nodeX - 1].Add(node);
			}
			return layers;
		}
		static List<Node> ProcessSingletons(List<List<Node>> layers)
		{
			if (_shouldSeparateByTechLevels) return new List<Node>();
			List<ResearchNode> singletons = new List<ResearchNode>();

			var length = layers[0].Count;
			List<Node> workingList = new List<Node>();
			for (int i = 0; i < length; i++)
			{
				var node = layers[0][i];

				if (node.OutEdges.Count > 0) workingList.Add(node);
				if (node is ResearchNode rNode && rNode.OutEdges.Count == 0) singletons.Add(rNode);
			}
			singletons.OrderBy(x => x.Research.techLevel);

			layers[0] = workingList;
			foreach (var g in singletons.GroupBy(n => n.Research.techLevel)) PlaceSingletons(g, layers.Count - 1);

			return new List<Node>(singletons);
		}
		static void PlaceSingletons(IEnumerable<Node> singletons, int colNum)
		{
			int x = 0, y = (int) mainGraphUpperbound;
			foreach (var n in singletons)
			{
				n.X = x + 1; n.Y = y;
				y += (x + 1) / colNum;
				x = (x + 1) % colNum;
			}
			mainGraphUpperbound = x == 0 ? y : y + 1;
		}
		public static void LegacyPreprocessing()
		{
			var layers = Layering(Nodes());
			var singletons = ProcessSingletons(layers);
			_layers = layers;
			_singletons = singletons;
		}
		public static void MainAlgorithm(List<List<Node>> data)
		{
			NodeLayers layers = new NodeLayers(data);
			List<NodeLayers> modsSplit = null;
						
			if (ModSettings_ResearchPowl.placeModTechSeparately) modsSplit = layers.SplitLargeMods();
			else
			{
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

			foreach (var layer in allLayers) OrganizeLayers(layer);
			
			PositionAllLayers(allLayers);
		}
		public static void OrganizeLayers(NodeLayers layers)
		{
			layers.MinimizeCrossings();
			layers.ApplyGridCoordinates();
			layers.ImproveNodePositionsInLayers();
		}
		static void FitLayersInBounds(NodeLayers layers, float[] topBounds)
		{
			float dy = -99999;
			for (int i = 0; i < layers.LayerCount(); ++i)
			{
				dy = Math.Max(dy, topBounds[i] - layers.TopPosition(i));
			}
			layers.MoveVertically(dy);
			for (int i = 0; i < layers.LayerCount(); ++i)
			{
				topBounds[i] = Math.Max(topBounds[i], layers.BottomPosition(i) + 1);
			}
		}
		public static void PositionAllLayers(IEnumerable<NodeLayers> layers)
		{
			Log.Debug("PotisionAllLayers: starting upper bound {0}", mainGraphUpperbound);
			float[] topBounds = new float[_layers.Count()];
			for (int i = 0; i < topBounds.Count(); ++i)
			{
				topBounds[i] = mainGraphUpperbound;
			}
			foreach (var layer in layers)
			{
				FitLayersInBounds(layer, topBounds);
			}
			mainGraphUpperbound = topBounds.Max();
		}
		public static void WaitForInitialization()
		{
			if (!Tree.Initialized)
			{
				if (ModSettings_ResearchPowl.delayLayoutGeneration) Tree.InitializeLayout();
				else if (ModSettings_ResearchPowl.asyncLoadingOnStartup)
				{
					while (!Tree.Initialized) continue;
				}
			}
		}
		public static bool ResetLayout() {
			if (Initializing) return false;
			Initializing = true;
			Initialized = false;
			InitializeNodesStructures();
			InitializeLayout();
			return true;
		}
		public static void InitializeLayout()
		{
			var timer = new System.Diagnostics.Stopwatch();
  			timer.Start();
			Initializing = true;
			mainGraphUpperbound = 1;

			// actually a lot of the initialization are done by the call of
			// `Nodes()` and `ResearchNodes()`

			LegacyPreprocessing();
			MainAlgorithm(_layers);
			RemoveEmptyRows();

			Tree.Size.z = (int) (Nodes().Max(n => n.Yf) + 0.01) + 1;
			Tree.Size.x = Nodes().Max(n => n.X);

			Log.Message("Research layout initialized", Tree.Size.x, Tree.Size.z);
			Log.Debug("Layout Size: x = {0}, y = {1}", Tree.Size.x, Tree.Size.z);
			Initialized = true;
			Initializing = false;
			timer.Stop();
			var timeTaken = timer.Elapsed;
			if (Prefs.DevMode) Log.Message("[ResearchPowl] Processed in " + timeTaken.ToString(@"ss\.fffff"));
		}
		static void RemoveEmptyRows()
		{
			var z = Nodes().Max(n => n.Yf);
			var y = 1;
			for (; y < z;) {
				var row = Row( y );
				if ( row.NullOrEmpty() ) {
					var ns = Nodes().Where(n => n.Yf > y).ToList();
					if (ns.Count() == 0) {
						break;
					}
					ns.ForEach(n => n.Yf = n.Yf - 1);
				}
				else
					++y;
			}
		}
		static void HorizontalPositions(List<ResearchNode> nodes)
		{
			_shouldSeparateByTechLevels = ModSettings_ResearchPowl.shouldSeparateByTechLevels;

			if (_shouldSeparateByTechLevels) HorizontalPositionsByTechLevels(nodes);
			else HorizontalPositionsByDensity(nodes);
		}
		static void HorizontalPositionsByTechLevels(List<ResearchNode> nodes) {
			_techLevelBounds = new Dictionary<TechLevel, IntRange>();
			float leftBound = 1;
			foreach (var group in
				nodes.GroupBy(n => n.Research.techLevel)
					 .OrderBy(g => g.Key)) {
				var updateOrder = FilteredTopoSort(
					group, n => n.Research.techLevel == group.Key);
				float newLeftBound  = leftBound;
				foreach (var node in updateOrder) {
					newLeftBound = Math.Max(newLeftBound, node.SetDepth((int)leftBound));
				}
				_techLevelBounds[group.Key] = new IntRange((int)leftBound - 1, (int)newLeftBound);
				leftBound = newLeftBound + 1;
			}
		}
		static void HorizontalPositionsByDensity(List<ResearchNode> nodes)
		{
			var updateOrder = TopologicalSort(nodes);
			foreach (var node in updateOrder) node.SetDepth(1);
		}
		static List<ResearchNode> TopologicalSort(IEnumerable<ResearchNode> nodes)
		{
			return FilteredTopoSort(nodes, n => true);
		}
		static List<ResearchNode> FilteredTopoSort(IEnumerable<ResearchNode> nodes, Func<ResearchNode, bool> p)
		{
			List<ResearchNode> result = new List<ResearchNode>();
			HashSet<ResearchNode> visited = new HashSet<ResearchNode>();
			foreach (var node in nodes)
			{
				if (node.OutNodes.OfType<ResearchNode>().Where(p).Any()) continue;
				FilteredTopoSortRec(node, p, result, visited);
			}
			return result;
		}
		static void FilteredTopoSortRec(ResearchNode cur, Func<ResearchNode, bool> p, List<ResearchNode> result, HashSet<ResearchNode> visited)
		{
			if (visited.Contains(cur)) return;
			foreach (var next in cur.InNodes.OfType<ResearchNode>().Where(p))
			{
				FilteredTopoSortRec(next, p, result, visited);
			}
			result.Add(cur);
			visited.Add(cur);
		}
		static void NormalizeEdges(List<Edge<Node, Node>> edges, List<Node> nodes)
		{
			foreach (var edge in new List<Edge<Node, Node>>(edges.Where(e => e.Span > 1)))
			{
				// remove and decouple long edge
				edges.Remove( edge );
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
					var dummyEdge = new Edge<Node, Node>(cur, dummy);
					cur.OutEdges.Add( dummyEdge );
					dummy.InEdges.Add( dummyEdge );
					nodes.Add( dummy );
					edges.Add( dummyEdge );
					cur = dummy;
				}

				// hook up final dummy to out node
				var finalEdge = new Edge<Node, Node>( cur, edge.Out );
				cur.OutEdges.Add( finalEdge );
				edge.Out.InEdges.Add( finalEdge );
				edges.Add( finalEdge );
			}
		}
		static List<Edge<Node, Node>> CreateEdges(List<ResearchNode> nodes)
		{
			// create links between nodes
			var edges = new List<Edge<Node, Node>>();

			foreach (var node in nodes)
			{
				if ( node.Research.prerequisites.NullOrEmpty() )
					continue;
				foreach ( var prerequisite in node.Research.prerequisites )
				{
					ResearchNode prerequisiteNode = nodes.Find(n => n.Research == prerequisite);
					if ( prerequisiteNode == null )
						continue;
					var edge = new Edge<Node, Node>( prerequisiteNode, node );
					edges.Add( edge );
					node.InEdges.Add( edge );
					prerequisiteNode.OutEdges.Add( edge );
				}
			}

			return edges;
		}
		static void CheckPrerequisites(List<ResearchNode> nodes)
		{
			var nodesQueue = new Queue<ResearchNode>(nodes);
			// remove redundant prerequisites
			while ( nodesQueue.Count > 0 )
			{
				var node = nodesQueue.Dequeue();
				if ( node.Research.prerequisites.NullOrEmpty() )
					continue;

				var ancestors = node.Research.prerequisites?.SelectMany( r => r.Ancestors() ).ToList();
				var redundant = ancestors.Intersect( node.Research.prerequisites );
				if ( redundant.Any() )
				{
					Log.Debug( "\tRedundant prerequisites for {0}: {1}", node.Research.LabelCap,
								 string.Join( ", ", redundant.Select( r => r.LabelCap ).ToArray() ) );
					foreach ( var redundantPrerequisite in redundant )
						node.Research.prerequisites.Remove( redundantPrerequisite );
				}
			}

			// fix bad techlevels
			nodesQueue = new Queue<ResearchNode>(nodes);
			while ( nodesQueue.Count > 0 )
			{
				var node = nodesQueue.Dequeue();
				if ( !node.Research.prerequisites.NullOrEmpty() )
					// warn and fix badly configured techlevels
					if ( node.Research.prerequisites.Any( r => r.techLevel > node.Research.techLevel ) )
					{
						Log.Debug( "\t{0} has a lower techlevel than (one of) its prerequisites",
									 node.Research.label );
						node.Research.techLevel = node.Research.prerequisites.Max( r => r.techLevel );

						// re-enqeue all descendants
						foreach ( var descendant in node.Descendants.OfType<ResearchNode>() )
							nodesQueue.Enqueue( descendant );
					}
			}
		}
		static void FixPrerequisites(ResearchProjectDef d)
		{
			if (d.prerequisites == null) d.prerequisites = d.hiddenPrerequisites;
			else if (d.hiddenPrerequisites != null) d.prerequisites = d.prerequisites.Concat(d.hiddenPrerequisites).ToList();
		}
		static void InitializeNodesStructures()
		{
			var nodes = PopulateNodes();
			Log.Debug("{0} valid nodes found in def database", nodes.Count());
			var allNodes = nodes.OfType<Node>().ToList();
			CheckPrerequisites(nodes);
			var edges = CreateEdges(nodes);
			Log.Debug("{0} edges created", edges.Count());

			HorizontalPositions(nodes);
			NormalizeEdges(edges, allNodes);
			Log.Debug("{0} nodes after adding dummies", allNodes.Count());

			_nodes = allNodes;
			_researchNodes = nodes;
			_edges = edges;
		}
		static List<ResearchNode> PopulateNodes()
		{
			var projects = DefDatabase<ResearchProjectDef>.AllDefsListForReading;

			if (ModSettings_ResearchPowl.dontIgnoreHiddenPrerequisites && !prerequisitesFixed)
			{
				foreach (var n in projects) FixPrerequisites(n);
				prerequisitesFixed = true;
			}

			// find hidden nodes (nodes that have themselves as a prerequisite)
			var hidden = projects.Where( p => p.prerequisites?.Contains( p ) ?? false ).ToList();

			// find locked nodes (nodes that have a hidden node as a prerequisite)
			var locked = projects.Where( p => p.Ancestors().Intersect( hidden ).Any() );

			if (ModSettings_ResearchPowl.dontShowUnallowedTech)
			{
				foreach (var n in projects)
				{
					if ((int)n.techLevel > ModSettings_ResearchPowl.maxAllowedTechLvl) hidden.Add(n);
				}
			}

			// populate all nodes
			var nodes = new List<ResearchNode>(DefDatabase<ResearchProjectDef>
				.AllDefsListForReading
				.Except( hidden )
				.Except( locked )
				.Select(def => new ResearchNode( def )));
			return nodes;
		}
		[Conditional( "DEBUG" )]
		internal static void DebugDraw()
		{
			foreach (var v in Nodes()) {
				foreach ( var w in v.OutNodes ) Widgets.DrawLine( v.Right, w.Left, Color.white, 1 );
			}
		}
		static public bool StopFixedHighlights()
		{
			bool success = fixedHighlightSets.Any();
			foreach (var n in fixedHighlightSets) n.Stop();
			fixedHighlightSets.Clear();
			return success;
		}
		static List<ResearchNode> FindHighlightsFrom(ResearchNode node)
		{
			List<ResearchNode> workingList = new List<ResearchNode>(node.MissingPrerequisites());
			List<ResearchNode> concatList = new List<ResearchNode>(node.Children());
			var length = concatList.Count;
			for (int i = 0; i < length; i++)
			{
				var tmp = concatList[i];
				if (tmp.Research.IsFinished) workingList.Add(tmp);
			}
			return workingList;
		}
		static void OverrideHighlight(ResearchNode node)
		{
			hoverHighlightSet?.Stop();
			hoverHighlightSet = RelatedNodeHighlightSet.HoverOn(node);
			hoverHighlightSet.Start();
		}
		static void HandleHoverHighlight(ResearchNode node, Vector2 mousePos)
		{
			if (node.MouseOver(mousePos)) OverrideHighlight(node);
		}
		public static void HandleFixedHighlight(ResearchNode node) {
			var i = fixedHighlightSets.FirstIndexOf(s => s.Causer() == node);
			Log.Debug("Fixed highlight index: {0}", i);
			if (i >= 0 && i < fixedHighlightSets.Count)
			{
				fixedHighlightSets[i].Stop();
				fixedHighlightSets.RemoveAt(i);
			} else {
				Log.Debug("Add fixed highlight caused by {0}", node.Research.label);
				var hl = RelatedNodeHighlightSet.FixHighlight(node);
				hl.Start();
				if (!Event.current.shift) {
					StopFixedHighlights();
				}
				fixedHighlightSets.Add(hl);
			}
		}
		static bool ContinueHoverHighlight(Vector2 mouse)
		{
			if (hoverHighlightSet == null) return false;
			if (hoverHighlightSet.TryStop(mouse))
			{
				hoverHighlightSet = null;
				return false;
			}
			return true;
		}
		public static void Draw( Rect visibleRect )
		{
			if (_shouldSeparateByTechLevels)
			{
				List<TechLevel> list3 = new List<TechLevel>(RelevantTechLevels());
				var tmp = list3.Count;
				for (int i = 0; i < tmp; i++)
				{
					DrawTechLevel(list3[i], visibleRect);
				}
			}

			var list = new List<Edge<Node, Node>>(Edges.OrderBy( e => e.DrawOrder));
			var length = list.Count;
			for (int i = 0; i < length; i++)
			{
				list[i].Draw(visibleRect);
			}

			TryModifySharedState();

			var evt = new Event(Event.current);

			//Compile list of drawn nodes
			List<ResearchNode> list2 = new List<ResearchNode>(ResearchNodes());
			bool hoverHighlight = ContinueHoverHighlight(evt.mousePosition);
			length = list2.Count;
			for (int i = 0; i < length; i++)
			{
				var node = list2[i];
				if (node.IsVisible(visibleRect))
				{
					if (!hoverHighlight) HandleHoverHighlight(node, evt.mousePosition);
					node.Draw(visibleRect, Painter.Tree);
				}
			}
		}
		private static void TryModifySharedState()
		{
			if (Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift)) DisplayProgressState = true;
			else if (Input.GetKeyUp(KeyCode.LeftShift) || Input.GetKeyUp(KeyCode.RightShift)) DisplayProgressState = false;
		}
		public static void DrawTechLevel( TechLevel techlevel, Rect visibleRect )
		{
			if (ModSettings_ResearchPowl.dontShowUnallowedTech && (int)techlevel > ModSettings_ResearchPowl.maxAllowedTechLvl) return;

			// determine positions
			if (TechLevelBounds().TryGetValue(techlevel, out IntRange bounds) == false) return;
			var xMin = ( NodeSize.x + NodeMargins.x ) * bounds.min - NodeMargins.x / 2f;
			var xMax = ( NodeSize.x + NodeMargins.x ) * bounds.max - NodeMargins.x / 2f;

			GUI.color   = Assets.TechLevelColor;
			Text.Anchor = TextAnchor.MiddleCenter;

			// lower bound
			if ( bounds.min > 0 && xMin > visibleRect.xMin && xMin < visibleRect.xMax )
			{
				// line
				Widgets.DrawLine( new Vector2( xMin, visibleRect.yMin ), new Vector2( xMin, visibleRect.yMax ), Assets.TechLevelColor, 1f );

				// label
				var labelRect = new Rect(
					xMin + TechLevelLabelSize.y / 2f - TechLevelLabelSize.x / 2f,
					visibleRect.center.y             - TechLevelLabelSize.y / 2f,
					TechLevelLabelSize.x,
					TechLevelLabelSize.y );

				VerticalLabel( labelRect, techlevel.ToStringHuman() );
			}

			// upper bound
			if ( bounds.max < Size.x && xMax > visibleRect.xMin && xMax < visibleRect.xMax )
			{
				// label
				var labelRect = new Rect(
					xMax - TechLevelLabelSize.y / 2f - TechLevelLabelSize.x / 2f,
					visibleRect.center.y             - TechLevelLabelSize.y / 2f,
					TechLevelLabelSize.x,
					TechLevelLabelSize.y );

				VerticalLabel( labelRect, techlevel.ToStringHuman() );
			}

			GUI.color = Color.white;
			Text.Anchor = TextAnchor.UpperLeft;
		}
		private static void VerticalLabel(Rect rect, string text)
		{
			// store the scaling matrix
			var matrix = GUI.matrix;

			// rotate and then apply the scaling
			GUI.matrix = Matrix4x4.identity;
			GUIUtility.RotateAroundPivot( -90f, rect.center );
			GUI.matrix = matrix * GUI.matrix;

			Widgets.Label(rect, text);

			// restore the original scaling matrix
			GUI.matrix = matrix;
		}
		public static List<Node> Layer( int depth, bool ordered = false )
		{
			if ( ordered && OrderDirty )
			{
				_nodes     = Nodes().OrderBy( n => n.X ).ThenBy( n => n.Y ).ToList();
				OrderDirty = false;
			}

			var length = Nodes().Count;
			List<Node> workingList = new List<Node>();
			for (int i = 0; i < length; i++)
			{
				var node = Nodes()[i];
				if (node.X == depth) workingList.Add(node);
			}
			return workingList;
		}
		public static List<Node> Row(int Y)
		{
			var length = Nodes().Count;
			List<Node> workingList = new List<Node>();
			for (int i = 0; i < length; i++)
			{
				var node = Nodes()[i];
				if (node.Y == Y) workingList.Add(node);
			}
			return workingList;
		}
		
		public new static string ToString()
		{
			var text = new StringBuilder();

			var length = Nodes().Max( n => n.X );
			for ( var l = 1; l <= length; l++ )
			{
				text.AppendLine( $"Layer {l}:" );
				var layer = Layer( l, true );

				foreach ( var n in layer )
				{
					text.AppendLine( $"\t{n}" );
					text.AppendLine( "\t\tAbove: " + string.Join( ", ", n.InNodes.Select( a => a.ToString() ).ToArray() ) );
					text.AppendLine( "\t\tBelow: " + string.Join( ", ", n.OutNodes.Select( b => b.ToString() ).ToArray() ) );
				}
			}

			return text.ToString();
		}
	}
}