﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Windows.Forms;
using System.Diagnostics;
using Newtonsoft.Json;
using System.Text;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Foreman
{
	public abstract class BaseNodeElement : GraphElement
	{
		public bool Highlighted = false; //selection - note that this doesnt mean it is or isnt in selection (at least not during drag operation - ex: dragging a not-selection over a group of selected nodes will change their highlight status, but wont add them to the 'selected' set until you let go of the drag)
		public ReadOnlyBaseNode DisplayedNode { get; private set; }

		public override int X { get { return DisplayedNode.Location.X; } set { Trace.Fail("Base node element location cant be set through X parameter! Use SetLocation(Point)"); } }
		public override int Y { get { return DisplayedNode.Location.Y; } set { Trace.Fail("Base node element location cant be set through Y parameter! Use SetLocation(Point)"); } }
		public override Point Location { get { return DisplayedNode.Location; } set { Trace.Fail("Base node element location cant be set through Location parameter! Use SetLocation(Point)"); } }
		public void SetLocation(Point location)
		{
			if (location != Location)
			{
				graphViewer.Graph.RequestNodeController(DisplayedNode).SetLocation(location);

				RequestStateUpdate();
				foreach (BaseNodeElement linkedNode in DisplayedNode.InputLinks.Select(l => graphViewer.LinkElementDictionary[l].SupplierElement))
					linkedNode.RequestStateUpdate();
				foreach (BaseNodeElement linkedNode in DisplayedNode.OutputLinks.Select(l => graphViewer.LinkElementDictionary[l].ConsumerElement))
					linkedNode.RequestStateUpdate();
			}
		}

		protected abstract Brush CleanBgBrush { get; }
		private static readonly Brush errorBgBrush = Brushes.Coral;
		private static readonly Brush ManualRateBGFilterBrush = new SolidBrush(Color.FromArgb(50, 0, 0, 0));

		private static readonly Brush equalFlowBorderBrush = Brushes.DarkGreen;
		private static readonly Brush overproducingFlowBorderBrush = Brushes.DarkGoldenrod;
		private static readonly Brush undersuppliedFlowBorderBrush = Brushes.DarkRed;

		protected static readonly Brush selectionOverlayBrush = new SolidBrush(Color.FromArgb(100, 100, 100, 200));

		protected static readonly Brush TextBrush = Brushes.Black;
		protected static readonly Font BaseFont = new Font(FontFamily.GenericSansSerif, 10f);
		protected static readonly Font TitleFont = new Font(FontFamily.GenericSansSerif, 9.2f, FontStyle.Bold);

		protected static StringFormat TitleFormat = new StringFormat() { LineAlignment = StringAlignment.Near, Alignment = StringAlignment.Center };
		protected static StringFormat TextFormat = new StringFormat() { LineAlignment = StringAlignment.Near, Alignment = StringAlignment.Center };

		//most values are attempted to fit the grid (6 * 2^n) - ex: 72 = 6 * (4+8)
		protected const int BaseSimpleHeight = 96; // 96 fits grid
		protected const int BaseRecipeHeight = 144; //144 fits grid
		protected const int TabPadding = 7; //makes each tab be evenly spaced for grid
		protected const int WidthD = 24; //(6*4) -> width will be divisible by this
		protected const int PassthroughNodeWidth = WidthD * 3;
		protected const int MinWidth = WidthD * 6;
		protected const int BorderSpacing = 1; //the drawn node will be smaller by this in all directions (graph looks nicer if adjacent nodes have a slight gap between them)

		protected List<ItemTabElement> InputTabs;
		protected List<ItemTabElement> OutputTabs;

		private Point MouseDownLocation; //location where the mouse click down first happened - in graph coordinates (used to ensure that any drag operation begins at the start, and not at the point (+- a few pixels) where the drag was officially registed as a drag and not just a mouse click.
		private Point MouseDownNodeLocation; //location of this node the moment the mouse click down first happened - in graph coordinates
		private bool DragStarted;

		private bool NodeStateRequiresUpdate; //these are set by the events called from the node (as well as calling for invalidation). Any paint call checks for these, and if true resets them to false and calls the appropriate update functions
		private bool NodeValuesRequireUpdate; //this removes the need to manually update the nodes after any change, as well as not spamming update calls after every change (being based on paint refresh - aka: when it actually matters)

		protected ErrorNoticeElement errorNotice;

		public BaseNodeElement(ProductionGraphViewer graphViewer, ReadOnlyBaseNode node) : base(graphViewer)
		{
			DisplayedNode = node;
			DragStarted = false;
			DisplayedNode.NodeStateChanged += DisplayedNode_NodeStateChanged;
			DisplayedNode.NodeValuesChanged += DisplayedNode_NodeValuesChanged;

			InputTabs = new List<ItemTabElement>();
			OutputTabs = new List<ItemTabElement>();

			errorNotice = new ErrorNoticeElement(graphViewer, this);
			errorNotice.Location = new Point(-Width / 2, -Height / 2);
			errorNotice.SetVisibility(false);

			//first stage item tab creation - absolutely necessary in the constructor due to the creation and simultaneous linking of nodes being possible (drag to new node for example).
			foreach (Item item in DisplayedNode.Inputs)
				InputTabs.Add(new ItemTabElement(item, LinkType.Input, base.graphViewer, this));
			foreach (Item item in DisplayedNode.Outputs)
				OutputTabs.Add(new ItemTabElement(item, LinkType.Output, base.graphViewer, this));
		}

		private void DisplayedNode_NodeStateChanged(object sender, EventArgs e) { NodeStateRequiresUpdate = true; graphViewer.Invalidate(); }
		private void DisplayedNode_NodeValuesChanged(object sender, EventArgs e) { NodeValuesRequireUpdate = true; graphViewer.Invalidate(); }

		public void RequestStateUpdate() { NodeStateRequiresUpdate = true; }

		protected virtual void UpdateState()
		{
			//update error notice
			errorNotice.SetVisibility(DisplayedNode.State == NodeState.Error || DisplayedNode.State == NodeState.Warning);
			errorNotice.X = -Width / 2;
			errorNotice.Y = -Height / 2;

			UpdateTabOrder();
		}

		protected virtual void UpdateValues()
		{
			//update tab values
			foreach (ItemTabElement tab in InputTabs)
				tab.UpdateValues(DisplayedNode.GetConsumeRate(tab.Item), 0, false); //for inputs we only care to display the supply rate (guaranteed by solver to be equal to the amount consumed by recipe)
			foreach (ItemTabElement tab in OutputTabs)
				tab.UpdateValues(DisplayedNode.GetSupplyRate(tab.Item), DisplayedNode.GetSupplyUsedRate(tab.Item), DisplayedNode.IsOverproducing(tab.Item)); //for outputs we want the amount produced by the node, the amount supplied to other nodes, and true if we are supplying less than producing.
		}

		private void UpdateTabOrder()
		{
			InputTabs = InputTabs.OrderBy(it => GetItemTabXHeuristic(it)).ThenBy(it => it.Item.Name).ToList(); //then by ensures same result no matter who came first
			OutputTabs = OutputTabs.OrderBy(it => GetItemTabXHeuristic(it)).ThenBy(it => it.Item.Name).ToList();

			int x = -GetIconWidths(OutputTabs) / 2;
			int y = DisplayedNode.NodeDirection == NodeDirection.Up ? (-Height / 2) + 1 : (Height / 2) - 1;
			foreach (ItemTabElement tab in OutputTabs)
			{
				x += TabPadding;
				tab.Location = new Point(x + (tab.Width / 2), y);
				x += tab.Width;
			}

			x = -GetIconWidths(InputTabs) / 2;
			y = DisplayedNode.NodeDirection == NodeDirection.Up ? (Height / 2) - 1 : (-Height / 2) + 1;
			foreach (ItemTabElement tab in InputTabs)
			{
				x += TabPadding;
				tab.Location = new Point(x + (tab.Width / 2), y);
				x += tab.Width;
			}
		}

		protected int GetIconWidths(List<ItemTabElement> tabs)
		{
			int result = TabPadding;
			foreach (ItemTabElement tab in tabs)
				result += tab.Bounds.Width + TabPadding;
			return result;
		}

		private int GetItemTabXHeuristic(ItemTabElement tab)
		{
			int total = 0;
			foreach (ReadOnlyNodeLink link in tab.Links)
			{
				Point diff = Point.Subtract(link.Supplier.Location, (Size)link.Consumer.Location);
				total += Convert.ToInt32(Math.Atan2(tab.LinkType == LinkType.Input? diff.X : -diff.X, diff.Y) * 1000 + (diff.Y > 0? 1 : 0)); //x needs to be flipped depending on which endpoint we are calculating for. y is absoluted to take care of down connections. slight addition in case of up connection ensures that 2 equal connections will prioritize the up over the down.
			}
			return total;
		}

		public ItemTabElement GetOutputLineItemTab(Item item)
		{
			if (NodeStateRequiresUpdate)
				UpdateState();
			NodeStateRequiresUpdate = false;

			return OutputTabs.First(it => it.Item == item);
		}
		public ItemTabElement GetInputLineItemTab(Item item)
		{
			if (NodeStateRequiresUpdate)
				UpdateState();
			NodeStateRequiresUpdate = false;

			return InputTabs.First(it => it.Item == item);
		}

		public override void UpdateVisibility(Rectangle graph_zone, int xborder = 0, int yborder = 0)
		{
			base.UpdateVisibility(graph_zone, xborder, yborder + 30); //account for the vertical item boxes
		}

		public override bool ContainsPoint(Point graph_point)
		{
			if (!Visible)
				return false;
			if (base.ContainsPoint(graph_point))
				return true;

			foreach (ItemTabElement tab in SubElements.OfType<ItemTabElement>())
				if (tab.ContainsPoint(graph_point))
					return true;
			if (errorNotice.ContainsPoint(graph_point))
				return true;

			return false;
		}

		public override void PrePaint()
		{
			if (NodeStateRequiresUpdate)
				UpdateState();
			if (NodeStateRequiresUpdate || NodeValuesRequireUpdate)
				UpdateValues();
			NodeStateRequiresUpdate = false;
			NodeValuesRequireUpdate = false;
		}

		protected override void Draw(Graphics graphics, NodeDrawingStyle style)
		{
			Point trans = LocalToGraph(new Point(0, 0)); //all draw operations happen in graph 0,0 origin coordinates. So we need to transform all our draw operations to the local 0,0 (center of object)
			if (style == NodeDrawingStyle.IconsOnly)
			{
				int iconSize = graphViewer.IconsDrawSize;
				if(NodeIcon() != null) graphics.DrawImage(NodeIcon(), trans.X - (iconSize / 2), trans.Y - (iconSize / 2), iconSize, iconSize);
			}
			else
			{
				//background
				Brush bgBrush = DisplayedNode.State == NodeState.Error ? errorBgBrush : CleanBgBrush;
				Brush borderBrush = DisplayedNode.ManualRateNotMet() ? undersuppliedFlowBorderBrush : DisplayedNode.IsOverproducing() ? overproducingFlowBorderBrush : equalFlowBorderBrush;

				GraphicsStuff.FillRoundRect(trans.X - (Width / 2) + BorderSpacing, trans.Y - (Height / 2) + BorderSpacing, Width - (2 * BorderSpacing), Height - (2 * BorderSpacing), 10, graphics, borderBrush); //flow status border

				int yoffset = (DisplayedNode.KeyNode && !(this is ConsumerNodeElement)) ? 15 : 0;
				int heightOffset = DisplayedNode.KeyNode ? (this is ConsumerNodeElement || this is SupplierNodeElement) ? 15 : 30 : 0;
				GraphicsStuff.FillRoundRect(trans.X - (Width / 2) + BorderSpacing + 3, trans.Y - (Height / 2) + BorderSpacing + 3 + yoffset, Width - (2 * BorderSpacing) - 6, Height - (2 * BorderSpacing) - 6 - heightOffset, 7, graphics, bgBrush); //basic background (with given background brush)
				if (DisplayedNode.RateType == RateType.Manual)
					GraphicsStuff.FillRoundRect(trans.X - (Width / 2) + 3, trans.Y - (Height / 2) + 3, Width - 6, Height - 6, 7, graphics, ManualRateBGFilterBrush); //darken background if its a manual rate set

				if (graphViewer.FlagOUSuppliedNodes && borderBrush != equalFlowBorderBrush)
					GraphicsStuff.FillRoundRectTLFlag(trans.X - (Width / 2) + 3, trans.Y - (Height / 2) + 3, Width / 2 - 6, Height / 2 - 6, 7, graphics, borderBrush); //supply flag
				if (DisplayedNode.State == NodeState.Warning)
					GraphicsStuff.FillRoundRectTLFlag(trans.X - (Width / 2) + 3, trans.Y - (Height / 2) + 3, Width / 2 - 6, Height / 2 - 6, 7, graphics, errorBgBrush); //warning flag

				//draw in all the inside details for this node
				if (style == NodeDrawingStyle.Regular || style == NodeDrawingStyle.PrintStyle)
					DetailsDraw(graphics, trans);

				//highlight
				if (Highlighted)
					GraphicsStuff.FillRoundRect(trans.X - (Width / 2), trans.Y - (Height / 2), Width, Height, 8, graphics, selectionOverlayBrush);
			}
		}

		protected abstract void DetailsDraw(Graphics graphics, Point trans); //draw the inside of the node.
		protected abstract Bitmap NodeIcon();

		public override List<TooltipInfo> GetToolTips(Point graph_point)
		{
			GraphElement element = SubElements.FirstOrDefault(it => it.ContainsPoint(graph_point));
			List<TooltipInfo> subTooltips = element?.GetToolTips(graph_point) ?? null;
			List<TooltipInfo> myTooltips = GetMyToolTips(graph_point, subTooltips == null || subTooltips.Count == 0);

			if (myTooltips == null)
				myTooltips = new List<TooltipInfo>();
			if (subTooltips != null)
				myTooltips.AddRange(subTooltips);

			return myTooltips;
		}

		protected abstract List<TooltipInfo> GetMyToolTips(Point graph_point, bool exclusive); //exclusive = true means no other tooltips are shown

		public override void MouseDown(Point graph_point, MouseButtons button)
		{
			MouseDownLocation = graph_point;
			MouseDownNodeLocation = new Point(X, Y);

			if (button == MouseButtons.Left)
				graphViewer.MouseDownElement = this;
		}

		public override void MouseUp(Point graph_point, MouseButtons button, bool wasDragged)
		{
			DragStarted = false;
			GraphElement subelement = SubElements.OfType<ItemTabElement>().FirstOrDefault(it => it.ContainsPoint(graph_point));
			if (!wasDragged)
			{
				if (subelement != null)
					subelement.MouseUp(graph_point, button, false);
				else if (errorNotice.ContainsPoint(graph_point))
					errorNotice.MouseUp(graph_point, button, false);
				else
					MouseUpAction(graph_point, button);
			}
		}

		protected virtual void MouseUpAction(Point graph_point, MouseButtons button)
		{
			if (button == MouseButtons.Left)
			{
				graphViewer.EditNode(this);
			}
			else if (button == MouseButtons.Right)
			{
				RightClickMenu.Items.Add(new ToolStripMenuItem("Delete node", null,
						new EventHandler((o, e) =>
						{
							RightClickMenu.Close();
							graphViewer.Graph.DeleteNode(DisplayedNode);
							graphViewer.Graph.UpdateNodeValues();
						})));
				if (graphViewer.SelectedNodes.Count > 1 && graphViewer.SelectedNodes.Contains(this))
				{
					RightClickMenu.Items.Add(new ToolStripMenuItem("Delete selected nodes", null,
						new EventHandler((o, e) =>
						{
							RightClickMenu.Close();
							graphViewer.TryDeleteSelectedNodes();
						})));
				}

				RightClickMenu.Items.Add(new ToolStripSeparator());

				RightClickMenu.Items.Add(new ToolStripMenuItem("Flip node", null,
					new EventHandler((o, e) =>
					{
						RightClickMenu.Close();
						graphViewer.Graph.RequestNodeController(DisplayedNode).SetDirection(DisplayedNode.NodeDirection == NodeDirection.Up ? NodeDirection.Down : NodeDirection.Up);
					})));
				if (graphViewer.SelectedNodes.Count > 1 && graphViewer.SelectedNodes.Contains(this))
				{
					RightClickMenu.Items.Add(new ToolStripMenuItem("Flip selected nodes", null,
						new EventHandler((o, e) =>
						{
							RightClickMenu.Close();
							graphViewer.FlipSelectedNodes();
						})));
				}

				if (graphViewer.SelectedNodes.Count > 0)
				{
					RightClickMenu.Items.Add(new ToolStripSeparator());
					RightClickMenu.Items.Add(new ToolStripMenuItem("Clear selection", null,
						new EventHandler((o, e) =>
						{
							RightClickMenu.Close();
							graphViewer.ClearSelection();
						})));
				}

				HashSet<Item> openInputs = new HashSet<Item>(graphViewer.SelectedNodes.SelectMany(n => n.InputTabs.Where(t => !t.Links.Any()).Select(t => t.Item)));
				HashSet<Item> openOutputs = new HashSet<Item>(graphViewer.SelectedNodes.SelectMany(n => n.OutputTabs.Where(t => !t.Links.Any()).Select(t => t.Item)));
				HashSet<Item> availableInputs = new HashSet<Item>(graphViewer.SelectedNodes.SelectMany(n => n.InputTabs.Select(t => t.Item)));
				HashSet<Item> availableOutputs = new HashSet<Item>(graphViewer.SelectedNodes.SelectMany(n => n.OutputTabs.Select(t => t.Item)));
				bool matchedIO = openInputs.Intersect(availableOutputs).Any();
				bool matchedOI = openOutputs.Intersect(availableInputs).Any();
				if(matchedIO || matchedOI)
				{
					RightClickMenu.Items.Add(new ToolStripSeparator());
					
					if(matchedIO)
					{
						RightClickMenu.Items.Add(new ToolStripMenuItem("Auto-connect disconnected inputs", null,
							new EventHandler((o, e) =>
							{
								RightClickMenu.Close();

								Dictionary<ReadOnlyBaseNode, List<Item>> openInputNodes = new Dictionary<ReadOnlyBaseNode, List<Item>>();
								foreach(BaseNodeElement node in graphViewer.SelectedNodes.Where(n => n.InputTabs.Any(t => !t.Links.Any())))
									openInputNodes.Add(node.DisplayedNode, node.InputTabs.Where(t => !t.Links.Any()).Select(t => t.Item).ToList());

								Dictionary<Item, List<ReadOnlyBaseNode>> availableOutputNodes = new Dictionary<Item, List<ReadOnlyBaseNode>>();
								foreach(ReadOnlyBaseNode node in graphViewer.SelectedNodes.Select(n => n.DisplayedNode).Where(n => !openInputNodes.ContainsKey(n)))
								{
									foreach(Item output in node.Outputs)
									{
										if (!availableOutputNodes.ContainsKey(output))
											availableOutputNodes.Add(output, new List<ReadOnlyBaseNode>());
										availableOutputNodes[output].Add(node);
									}
								}

								foreach(ReadOnlyBaseNode node in openInputNodes.Keys)
								{
									foreach (Item requiredInput in openInputNodes[node])
									{
										if (availableOutputNodes.ContainsKey(requiredInput))
										{
											ReadOnlyBaseNode linkNode = availableOutputNodes[requiredInput].OrderBy(n => Math.Abs(node.Location.X - n.Location.X) + Math.Abs(node.Location.Y - n.Location.Y)).FirstOrDefault();
											if (linkNode != null)
												graphViewer.Graph.CreateLink(linkNode, node, requiredInput);
										}
									}
								}

								graphViewer.Graph.UpdateNodeValues();
							})));
					}
					if (matchedOI)
					{
						RightClickMenu.Items.Add(new ToolStripMenuItem("Auto-connect disconnected outputs", null,
							new EventHandler((o, e) =>
							{
								RightClickMenu.Close();

								Dictionary<ReadOnlyBaseNode, List<Item>> openOutputNodes = new Dictionary<ReadOnlyBaseNode, List<Item>>();
								foreach (BaseNodeElement node in graphViewer.SelectedNodes.Where(n => n.OutputTabs.Any(t => !t.Links.Any())))
									openOutputNodes.Add(node.DisplayedNode, node.OutputTabs.Where(t => !t.Links.Any()).Select(t => t.Item).ToList());

								Dictionary<Item, List<ReadOnlyBaseNode>> availableInputNodes = new Dictionary<Item, List<ReadOnlyBaseNode>>();
								foreach (ReadOnlyBaseNode node in graphViewer.SelectedNodes.Select(n => n.DisplayedNode).Where(n => !openOutputNodes.ContainsKey(n)))
								{
									foreach (Item input in node.Inputs)
									{
										if (!availableInputNodes.ContainsKey(input))
											availableInputNodes.Add(input, new List<ReadOnlyBaseNode>());
										availableInputNodes[input].Add(node);
									}
								}

								foreach (ReadOnlyBaseNode node in openOutputNodes.Keys)
								{
									foreach (Item requiredOutput in openOutputNodes[node])
									{
										if (availableInputNodes.ContainsKey(requiredOutput))
										{
											ReadOnlyBaseNode linkNode = availableInputNodes[requiredOutput].OrderBy(n => Math.Abs(node.Location.X - n.Location.X) + Math.Abs(node.Location.Y - n.Location.Y)).FirstOrDefault();
											if (linkNode != null)
												graphViewer.Graph.CreateLink(node, linkNode, requiredOutput);
										}
									}
								}

								graphViewer.Graph.UpdateNodeValues();
							})));
					}
				}

				AddRClickMenuOptions(graphViewer.SelectedNodes.Count == 0 || graphViewer.SelectedNodes.Contains(this));

				RightClickMenu.Items.Add(new ToolStripSeparator());
				RightClickMenu.Items.Add(new ToolStripMenuItem("Copy key node status", null,
					new EventHandler((o, e) =>
					{
						RightClickMenu.Close();
						StringBuilder stringBuilder = new StringBuilder();
						var writer = new JsonTextWriter(new StringWriter(stringBuilder));

						JsonSerializer serialiser = JsonSerializer.Create();
						serialiser.Formatting = Formatting.None;
						serialiser.Serialize(writer, new Tuple<bool, string>(DisplayedNode.KeyNode, DisplayedNode.KeyNodeTitle));

						Clipboard.SetText(stringBuilder.ToString());

					})));

				if (graphViewer.SelectedNodes.Count == 0 || graphViewer.SelectedNodes.Contains(this))
				{
					try
					{
						JObject keyNodeStatus = JObject.Parse(Clipboard.GetText());
						if (keyNodeStatus["Item1"] != null && keyNodeStatus["Item2"] != null)
						{
							bool keyNode = (bool)keyNodeStatus["Item1"];
							string keyNodeTitle = (string)keyNodeStatus["Item2"];
							RightClickMenu.Items.Add(new ToolStripMenuItem("Paste key node status", null,
								new EventHandler((o, e) =>
								{
									RightClickMenu.Close();
									if(graphViewer.SelectedNodes.Count == 0)
									{
										BaseNodeController controller = graphViewer.Graph.RequestNodeController(this.DisplayedNode);
										controller.SetKeyNode(keyNode);
										controller.SetKeyNodeTitle(keyNodeTitle);
									}
									else if (graphViewer.SelectedNodes.Contains(this))
									{
										foreach (BaseNodeElement node in graphViewer.SelectedNodes)
										{
											BaseNodeController controller = graphViewer.Graph.RequestNodeController(node.DisplayedNode);
											controller.SetKeyNode(keyNode);
											controller.SetKeyNodeTitle(keyNodeTitle);
										}
									}
								})));
						}
					}
					catch { }
				}


				RightClickMenu.Show(graphViewer, graphViewer.GraphToScreen(graph_point));
			}
		}

		protected virtual void AddRClickMenuOptions(bool nodeInSelection) { }

		public override void Dragged(Point graph_point)
		{
			if (!DragStarted)
			{
				ItemTabElement draggedTab = null;
				foreach (ItemTabElement tab in SubElements.OfType<ItemTabElement>())
					if (tab.ContainsPoint(MouseDownLocation))
						draggedTab = tab;
				if (draggedTab != null)
					graphViewer.StartLinkDrag(this, draggedTab.LinkType, draggedTab.Item);
				else
				{
					DragStarted = true;
				}
			}
			else //drag started -> proceed with dragging the node around
			{
				Size offset = (Size)Point.Subtract(graph_point, (Size)MouseDownLocation);
				Point newLocation = graphViewer.Grid.AlignToGrid(Point.Add(MouseDownNodeLocation, offset));
				if (graphViewer.Grid.LockDragToAxis)
				{
					Point lockedDragOffset = Point.Subtract(graph_point, (Size)graphViewer.Grid.DragOrigin);

					if (Math.Abs(lockedDragOffset.X) > Math.Abs(lockedDragOffset.Y))
						newLocation.Y = graphViewer.Grid.DragOrigin.Y;
					else
						newLocation.X = graphViewer.Grid.DragOrigin.X;
				}

				if (Location != newLocation)
				{
					SetLocation(newLocation);

					this.UpdateTabOrder();
					foreach (ReadOnlyBaseNode node in DisplayedNode.InputLinks.Select(l => l.Supplier))
						graphViewer.NodeElementDictionary[node].UpdateTabOrder();
					foreach (ReadOnlyBaseNode node in DisplayedNode.OutputLinks.Select(l => l.Consumer))
						graphViewer.NodeElementDictionary[node].UpdateTabOrder();
				}
			}
		}
	}
}