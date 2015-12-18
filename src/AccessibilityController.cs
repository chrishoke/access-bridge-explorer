﻿// Copyright 2015 Google Inc. All Rights Reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using AccessBridgeExplorer.Model;
using AccessBridgeExplorer.WindowsAccessBridge;

namespace AccessBridgeExplorer {

  public interface IUiThreadInvoker : IWin32Window {
    void InvokeLater(Action action);
    void Invoke(Action action);
    T Compute<T>(Func<T> function);
  }

  public class AccessibilityController : IDisposable {
    private readonly IUiThreadInvoker _invoker;
    private readonly TreeView _accessibilityTree;
    private readonly PropertyListViewWrapper _acessibleContextPropertyListWrapper;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ListView _accessibilityEventList;
    private readonly ListView _messageList;
    private readonly ToolStripMenuItem _eventsMenu;
    private readonly ToolStripMenuItem _propertyOptionsMenu;
    private readonly AccessBridge _accessBridge = new AccessBridge();
    private readonly OverlayWindow _overlayWindow = new OverlayWindow();
    private readonly TooltipWindow _tooltipWindow = new TooltipWindow();
    private bool _overlayWindowEnabled;
    private Rectangle? _overlayWindowRectangle;
    private bool _disposed;
    private int _eventId;
    private int _messageId;

    public AccessibilityController(
      IUiThreadInvoker invoker,
      TreeView accessibilityTree,
      PropertyListViewWrapper accessibleContextPropertyListWrapper,
      ToolStripStatusLabel statusLabel,
      ListView accessibilityEventList,
      ListView messageList,
      ToolStripMenuItem eventsMenu,
      ToolStripMenuItem propertyOptionsMenu) {
      _invoker = invoker;
      _accessibilityTree = accessibilityTree;
      _acessibleContextPropertyListWrapper = accessibleContextPropertyListWrapper;
      _statusLabel = statusLabel;
      _accessibilityEventList = accessibilityEventList;
      _messageList = messageList;
      _eventsMenu = eventsMenu;
      _propertyOptionsMenu = propertyOptionsMenu;
      _overlayWindowEnabled = true;

      _accessibilityTree.AfterSelect += AccessibilityTreeAfterSelect;
      _accessibilityTree.BeforeExpand += AccessibilityTreeBeforeExpand;
      _accessibilityTree.KeyDown += AccessibilityTreeOnKeyDown;

      _accessibilityEventList.MouseDoubleClick += AccessibilityEventListOnMouseDoubleClick;
      _accessibilityEventList.KeyDown += AccessibilityEventListOnKeyDown;

      PropertyOptions = PropertyOptions.AccessibleContextInfo |
        PropertyOptions.AccessibleIcons |
        PropertyOptions.AccessibleKeyBindings |
        PropertyOptions.AccessibleRelationSet |
        PropertyOptions.ParentContext |
        PropertyOptions.TopLevelWindowInfo |
        PropertyOptions.AccessibleText |
        PropertyOptions.AccessibleValue |
        PropertyOptions.AccessibleSelection |
        PropertyOptions.AccessibleTable |
        PropertyOptions.AccessibleActions;

      CreateEventMenuItems();
      CreatePropertyOptionsMenuItems();
    }

    public PropertyOptions PropertyOptions { get; set; }

    private void AccessibilityEventListOnKeyDown(object sender, KeyEventArgs e) {
      if (_accessibilityEventList.SelectedItems.Count == 0)
        return;

      switch (e.KeyCode & Keys.KeyCode) {
        case Keys.Return:
          foreach (ListViewItem item in _accessibilityEventList.SelectedItems) {
            ShowEvent(item);
          }
          break;
      }
    }

    private void AccessibilityEventListOnMouseDoubleClick(object sender, MouseEventArgs e) {
      ListViewHitTestInfo info = _accessibilityEventList.HitTest(e.X, e.Y);
      if (info.Location == ListViewHitTestLocations.None)
        return;
      ShowEvent(info.Item);
    }

    private void ShowEvent(ListViewItem item) {
      var eventInfo = item.Tag as EventInfo;
      if (eventInfo == null)
        return;

      if (eventInfo.OnDisplay == null)
        return;

      try {
        eventInfo.OnDisplay();
      } catch (Exception e) {
        LogErrorMessage(e);
        MessageBox.Show(_invoker, e.Message, @"Error displaying event data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void ShowEventDialog(AccessibleContextNode accessibleContextNode) {
      var form = new EventForm();
      form.SetContextNodePropertyList(accessibleContextNode.GetProperties(PropertyOptions));
      form.ContextNodeSelect += (sender, args) => SelectTreeNode(accessibleContextNode);
      form.ShowDialog(_invoker);
    }

    private void ShowEventDialog(AccessibleContextNode accessibleContextNode, AccessibleContextNode oldNode, AccessibleContextNode newNode) {
      var form = new EventForm();
      form.SetContextNodePropertyList(accessibleContextNode.GetProperties(PropertyOptions));
      form.SetOldValuePropertyList(oldNode.GetProperties(PropertyOptions));
      form.SetNewValuePropertyList(newNode.GetProperties(PropertyOptions));
      form.ContextNodeSelect += (sender, args) => SelectTreeNode(accessibleContextNode);
      form.OldValueSelect += (sender, args) => SelectTreeNode(oldNode);
      form.NewValueSelect += (sender, args) => SelectTreeNode(newNode);
      form.ShowDialog(_invoker);
    }

    private void ShowEventDialog(AccessibleContextNode accessibleContextNode, string oldValueName, string oldValue, string newValueName, string newValue) {
      var form = new EventForm();
      form.SetContextNodePropertyList(accessibleContextNode.GetProperties(PropertyOptions));
      form.SetOldValuePropertyList(new PropertyList { new PropertyNode(oldValueName, oldValue) });
      form.SetNewValuePropertyList(new PropertyList { new PropertyNode(newValueName, newValue) });
      form.ContextNodeSelect += (sender, args) => SelectTreeNode(accessibleContextNode);
      form.ShowDialog(_invoker);
    }

    private void CreateEventMenuItems() {
      var publicMembers = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public;
      int index = 0;
      foreach (var evt in _accessBridge.Events.GetType().GetEvents(publicMembers)) {
        CreateEventMenuItem(evt, index);
        index++;
      }
    }

    private void CreateEventMenuItem(System.Reflection.EventInfo evt, int index) {
      var privateMembers = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.NonPublic;
      var name = evt.Name;

      // Create menu item (fixed font for alignment)
      var item = new ToolStripMenuItem();
      item.Font = new Font("Lucida Console", _eventsMenu.Font.SizeInPoints, _eventsMenu.Font.Style, GraphicsUnit.Point);
      char mnemonicCharacter = (char)(index < 10 ? '0' + index : 'A' + index - 10);
      item.Text = string.Format("&{0} - {1}", mnemonicCharacter, name);
      item.CheckOnClick = true;
      item.CheckState = CheckState.Unchecked;
      _eventsMenu.DropDownItems.Add(item);

      // Find event handler
      var handlerMethod = GetType().GetMethod("EventsOn" + evt.Name, privateMembers);
      if (handlerMethod == null) {
        throw new ApplicationException(string.Format("Type \"{0}\" should contain a method named \"{1}\"",
          GetType().Name, "EventsOn" + evt.Name));
      }
      var handlerDelegate = Delegate.CreateDelegate(evt.EventHandlerType, this, handlerMethod);

      // Create click handler
      item.CheckedChanged += (sender, args) => {
        if (item.Checked) {
          // Add handler
          evt.AddEventHandler(_accessBridge.Events, handlerDelegate);
        } else {
          // Remove handler
          evt.RemoveEventHandler(_accessBridge.Events, handlerDelegate);
        }
      };
    }

    private void CreatePropertyOptionsMenuItems() {
      int index = 0;
      foreach (var field in typeof(PropertyOptions).GetFields(BindingFlags.Static | BindingFlags.Public)) {
        CreatePropertyOptionsMenuItem(field, index);
        index++;
      }
    }

    private void CreatePropertyOptionsMenuItem(FieldInfo field, int index) {
      var name = field.Name;
      var value = (PropertyOptions)field.GetValue(null);

      // Create menu item (fixed font for alignment)
      var item = new ToolStripMenuItem();
      item.Font = new Font("Lucida Console", _eventsMenu.Font.SizeInPoints, _eventsMenu.Font.Style, GraphicsUnit.Point);
      char mnemonicCharacter = (char)(index < 10 ? '0' + index : 'A' + index - 10);
      item.Text = string.Format("&{0} - {1}", mnemonicCharacter, name);
      item.CheckOnClick = true;
      item.CheckState = ((PropertyOptions & value) == 0 ? CheckState.Unchecked : CheckState.Checked);
      _propertyOptionsMenu.DropDownItems.Add(item);

      // Create click handler
      item.CheckedChanged += (sender, args) => {
        if (item.Checked) {
          PropertyOptions |= value;
        } else {
          PropertyOptions &= ~value;
        }
      };
    }

    public void Initialize() {
      _overlayWindow.TopMost = true;
      _overlayWindow.Visible = true;
      _overlayWindow.Size = new Size(0, 0);
      _overlayWindow.Location = new Point(-10, -10);
      _overlayWindow.Shown += (sender, args) => _accessibilityTree.Focus();

      _tooltipWindow.TopMost = true;
      _tooltipWindow.Visible = true;
      _tooltipWindow.Size = new Size(0, 0);
      _tooltipWindow.Location = new Point(-10, -10);
      _tooltipWindow.Shown += (sender, args) => _accessibilityTree.Focus();

      LogMessage("Initializing Java Access Bridge and enumerating active Java application windows.");
      UiAction(() => {
        //TODO: We initialize now so that the access bridge DLL has time to
        // discover the list of JVMs by the time we enumerate all windows.
        _accessBridge.Initialize();
      });
    }

    public void Dispose() {
      if (_disposed)
        return;

      DisposeTreeNodeList(_accessibilityTree.Nodes);
      _accessBridge.Dispose();
      _disposed = true;
    }

    public void UiAction(Action action) {
      _invoker.Invoke(() => {
        try {
          action();
        } catch (Exception e) {
          LogErrorMessage(e);
        }
      });
    }

    public T UiCompute<T>(Func<T> function) {
      return _invoker.Compute(() => {
        try {
          return function();
        } catch (Exception e) {
          LogErrorMessage(e);
          return default(T);
        }
      });
    }

    public class EventInfo {
      public EventInfo() {
      }

      public EventInfo(string eventName, AccessibleNode source, string oldValue = "", string newValue = "") {
        EventName = eventName;
        Source = source;
        OldValue = oldValue;
        NewValue = newValue;
      }

      public string EventName { get; set; }
      public AccessibleNode Source { get; set; }
      public string OldValue { get; set; }
      public string NewValue { get; set; }
      public Action OnDisplay { get; set; }
    }

    public void LogEvent(EventInfo eventInfo) {
      _eventId++;
      var time = DateTime.Now;
      ListViewItem item = new ListViewItem();
      item.Text = _eventId.ToString();
      item.SubItems.Add(time.ToLongTimeString());
      item.SubItems.Add(eventInfo.Source.JvmId.ToString());
      item.SubItems.Add(eventInfo.EventName);
      item.SubItems.Add(eventInfo.Source.GetTitle());
      item.SubItems.Add(eventInfo.OldValue);
      item.SubItems.Add(eventInfo.NewValue);
      item.Tag = eventInfo;
      AddEvent(item);
    }

    public void LogErrorEvent(string eventName, Exception error) {
      _eventId++;
      var time = DateTime.Now;
      ListViewItem item = new ListViewItem();
      item.Text = _eventId.ToString();
      item.SubItems.Add(time.ToLongTimeString());
      item.SubItems.Add("-");
      item.SubItems.Add(eventName);
      item.SubItems.Add(error.Message);

      AddEvent(item);
    }

    public void LogNodeEvent(string eventName, Func<AccessibleContextNode> factory, Action onDisplayAction = null) {
      try {
        LogEvent(new EventInfo {
          EventName = eventName,
          Source = factory(),
          OnDisplay = onDisplayAction
        });
      } catch (Exception e) {
        LogErrorEvent(eventName, e);
      }
    }

    public void LogNodeEvent(string eventName, Func<Tuple<AccessibleContextNode, string, string>> factory, Action onDisplayAction = null) {
      try {
        var result = factory();
        LogEvent(new EventInfo {
          EventName = eventName,
          Source = result.Item1,
          OldValue = result.Item2,
          NewValue = result.Item3,
          OnDisplay = onDisplayAction
        });
      } catch (Exception e) {
        LogErrorEvent(eventName, e);
      }
    }

    private void AddEvent(ListViewItem item) {
      AddListViewItem(_accessibilityEventList, item);
    }

    public void LogMessage(string format, params object[] args) {
      _messageId++;
      var time = DateTime.Now;
      ListViewItem item = new ListViewItem();
      item.Text = _messageId.ToString();
      item.SubItems.Add(time.ToLongTimeString());
      item.SubItems.Add(string.Format(format, args));
      AddListViewItem(_messageList, item);
    }

    public void LogErrorMessage(Exception error) {
      for (var current = error; current != null; current = current.InnerException) {
        LogMessage("{0}{1}", (current == error ? "ERROR: " : "      "), current.Message);
      }
    }

    private static void AddListViewItem(ListView listview, ListViewItem item) {
      listview.BeginUpdate();
      try {
        // Manage list overflow
        if (listview.Items.Count >= 1000) {
          for (var i = 0; i < 100; i++) {
            listview.Items.RemoveAt(0);
          }
        }
        // Add item and make it visible (scrolling).
        listview.Items.Add(item);
        item.EnsureVisible();
      } finally {
        listview.EndUpdate();
      }
    }

    public void RefreshTree() {
      if (_disposed)
        return;

      UiAction(() => {
        var windows = _accessBridge.EnumWindows();
        _accessibilityTree.BeginUpdate();
        try {
          DisposeTreeNodeList(_accessibilityTree.Nodes);
          _accessibilityTree.Nodes.Clear();
          if (!windows.Any()) {
            _accessibilityTree.Nodes.Add("No JVM/Java window found. Try Refresh Again?");
            return;
          }
          var topLevelNodes = windows.Select(x => new AccessibleNodeModel(x));
          topLevelNodes.ForEach(x => {
            var node = x.CreateTreeNode();
            _accessibilityTree.Nodes.Add(node);
            node.Expand();
          });
        } finally {
          _accessibilityTree.EndUpdate();
        }
        _statusLabel.Text = "Ready.";
        HideOverlayWindow();
        HideToolTip();
      });
    }

    private static void DisposeTreeNodeList(TreeNodeCollection list) {
      foreach (TreeNode node in list) {
        DisposeTreeNode(node);
      }
    }

    private static void DisposeTreeNode(TreeNode node) {
      DisposeTreeNodeList(node.Nodes);
      var model = node.Tag as AccessibleNodeModel;
      if (model != null) {
        model.AccessibleNode.Dispose();
      }
    }

    public void ClearSelectedNode() {
      var node = _accessibilityTree.SelectedNode;
      if (node != null) {
        _accessibilityTree.SelectedNode = null;
        _overlayWindowRectangle = null;
        UpdateOverlayWindow();
        _acessibleContextPropertyListWrapper.Clear();
      }
    }

    private void AccessibilityTreeBeforeExpand(object sender, TreeViewCancelEventArgs e) {
      var node = e.Node.Tag as NodeModel;
      if (node == null)
        return;
      UiAction(() => {
        node.BeforeExpand(sender, e);
      });
    }

    private void AccessibilityTreeAfterSelect(object sender, TreeViewEventArgs e) {
      var node = e.Node.Tag as AccessibleNodeModel;
      if (node == null) {
        _overlayWindowRectangle = null;
        UpdateOverlayWindow();
        _acessibleContextPropertyListWrapper.Clear();
        return;
      }

      _overlayWindowRectangle = null;
      UiAction(() => {
        _overlayWindowRectangle = node.AccessibleNode.GetScreenRectangle();
        var propertyList = node.AccessibleNode.GetProperties(PropertyOptions);
        _acessibleContextPropertyListWrapper.SetPropertyList(propertyList);
      });

      EnsureNodeVisible(e.Node);
      UpdateOverlayWindow();
    }

    private void AccessibilityTreeOnKeyDown(object sender, KeyEventArgs keyEventArgs) {
      if (keyEventArgs.KeyCode != Keys.Return)
        return;
      var treeNode = _accessibilityTree.SelectedNode;
      if (treeNode == null)
        return;

      var nodeModel = _accessibilityTree.SelectedNode.Tag as AccessibleNodeModel;
      if (nodeModel == null)
        return;

      UiAction(() => {
        // First thing first; Tell the node to forget about what it knows
        nodeModel.AccessibleNode.Refresh();

        // Update the treeview children so they get refreshed
        var expanded = treeNode.IsExpanded;
        if (expanded) {
          treeNode.Collapse();
        }
        nodeModel.ResetChildren(treeNode);
        if (expanded) {
          treeNode.Expand();
        }

        // Update the property list
        var propertyList = nodeModel.AccessibleNode.GetProperties(PropertyOptions);
        _acessibleContextPropertyListWrapper.SetPropertyList(propertyList);

        // Update the overlay window
        _overlayWindowRectangle = nodeModel.AccessibleNode.GetScreenRectangle();
        UpdateOverlayWindow();
      });
    }

    private void EnsureNodeVisible(TreeNode node) {
      if (node.Level >= 2) {
        node.EnsureVisible();
      }
    }

    private void UpdateOverlayWindow() {
      if (_overlayWindowRectangle == null || !_overlayWindowEnabled) {
        HideOverlayWindow();
        return;
      }

      if (!_overlayWindow.Visible) {
        _overlayWindow.TopMost = true;
        _overlayWindow.Visible = true;
      }

      // Note: The _overlayWindowRectangle value comes from the Java Access
      // Bridge for a given accessible component. Sometimes, the component has
      // bounds that extend outside of the visible screen (for example, an
      // editor embedded in a viewport will report negative values for y index
      // when scrolled down to the end of the text). On the other side, Windows
      // (or WinForms) limits the size of windows to the height/width of the
      // desktop. So, an a 1600x1200 screen, a rectangle of [x=10, y=-2000,
      // w=600, h=3000] is cropped to [x=10, y=-2000, w=600, h=1200]. This
      // results in a window that is not visible (y + height < 0).
      // to workaround that issue, we do some math. to make the rectable visible.
      var rect = _overlayWindowRectangle.Value;
      var invisibleX = rect.X < 0 ? -rect.X : 0;
      if (invisibleX > 0) {
        rect.X = 0;
        rect.Width -= invisibleX;
      }
      var invisibleY = rect.Y < 0 ? -rect.Y : 0;
      if (invisibleY > 0) {
        rect.Y = 0;
        rect.Height -= invisibleY;
      }
      _overlayWindow.Location = rect.Location;
      _overlayWindow.Size = rect.Size;
    }

    public void HideOverlayWindow() {
      _overlayWindow.Location = new Point(-10, -10);
      _overlayWindow.Size = new Size(0, 0);
    }

    public void EnableOverlayWindow(bool enabled) {
      _overlayWindowEnabled = enabled;
      UpdateOverlayWindow();
    }

    /// <summary>
    /// Return the <see cref="NodePath"/> of a node given a location on screen.
    /// Return <code>null</code> if there is no node at that location.
    /// </summary>
    public NodePath GetNodePathAt(Point screenPoint) {
      return UiCompute(() => {
        foreach (TreeNode treeNode in _accessibilityTree.Nodes) {
          var node = treeNode.Tag as AccessibleNodeModel;
          if (node == null)
            continue;

          var result = node.AccessibleNode.GetNodePathAt(screenPoint);
          if (result != null)
            return result;
        }

        return null;
      });
    }

    public void ShowOverlayForNodePath(NodePath path) {
      _overlayWindowRectangle = UiCompute(() => path.LeafNode.GetScreenRectangle());
      UpdateOverlayWindow();
    }

    public void RefreshTick() {
      if (_accessibilityTree.Nodes.Count == 0)
        RefreshTree();
    }

    public void SelectNodeAtPoint(Point screenPoint) {
      var nodePath = GetNodePathAt(screenPoint);
      if (nodePath == null) {
        LogMessage("No Accessible component found at mouse location {0}", screenPoint);
        return;
      }
      SelectTreeNode(nodePath);
      _accessibilityTree.Focus();
    }

    public void SelectTreeNode(AccessibleNode childNode) {
      UiAction(() => {
        var path = new NodePath();
        while (childNode != null) {
          path.AddParent(childNode);
          childNode = childNode.GetParent();
        }
        SelectTreeNode(path);
      });
    }

    public void SelectTreeNode(NodePath nodePath) {
      TreeNode lastFoundTreeNode = null;
      // Pop each node and find it in the corresponding collection
      var parentNodeList = _accessibilityTree.Nodes;
      while (nodePath.Count > 0) {
        var childNode = nodePath.Pop();

        var childTreeNode = FindTreeNodeInList(parentNodeList, childNode);
        if (childTreeNode == null) {
          LogMessage("Error finding child node in node list: {0}", childNode);
          break;
        }
        lastFoundTreeNode = childTreeNode;

        if (nodePath.Count == 0) {
          // Expand the whole subtree to force each node to refresh their value
          // in case the sub-tree disappears from the accessible application
          // (e.g. in the case of an ephemeral window).
          childNode.FetchSubTree();
          break;
        }

        childTreeNode.Expand();
        parentNodeList = childTreeNode.Nodes;
      }

      if (lastFoundTreeNode != null) {
        _accessibilityTree.SelectedNode = lastFoundTreeNode;
        EnsureNodeVisible(lastFoundTreeNode);
      }
    }

    private TreeNode FindTreeNodeInList(TreeNodeCollection list, AccessibleNode node) {
      return UiCompute(() => {
        // Search by child index (for transient nodes)
        var childIndex = node.GetIndexInParent();
        if (childIndex >= 0 && childIndex < list.Count) {
          return list[childIndex];
        }

        // Sequential search (for Jvm, Window nodes).
        foreach (TreeNode treeNode in list) {
          var childNode = treeNode.Tag as AccessibleNodeModel;
          if (childNode == null)
            continue;

          if (childNode.AccessibleNode.Equals(node)) {
            return treeNode;
          }
        }
        return null;
      });
    }

    public void ShowToolTip(Point screenPoint, NodePath nodePath) {
      UiAction(() => {
        var node = nodePath.LeafNode;

        var sb = new StringBuilder();
        foreach (var x in node.GetToolTipProperties()) {
          if (sb.Length > 0)
            sb.Append("\r\n");
          sb.AppendFormat("{0}: {1}", x.Name, x.Value);
        }
        _tooltipWindow.AutoSize = true;
        _tooltipWindow.Label.Text = string.Format("{0}", sb);
        _tooltipWindow.Location = new Point(
          Math.Max(0, screenPoint.X - _tooltipWindow.Size.Width - 16),
          Math.Max(0, screenPoint.Y - _tooltipWindow.Size.Height / 2));
        //_tooltipWindow.Location = new Point(
        //  Math.Max(0, screenPoint.X + 20),
        //  Math.Max(0, screenPoint.Y - _tooltipWindow.Size.Height / 2));
      });
    }

    public void HideToolTip() {
      _tooltipWindow.Label.Text = "";
      _tooltipWindow.Location = new Point(-10, -10);
      _tooltipWindow.Size = new Size(0, 0);
    }

    public void OnFocusLost() {
      HideOverlayWindow();
    }

    public void OnFocusGained() {
      UpdateOverlayWindow();
    }

    #region Event Handlers
    // ReSharper disable UnusedMember.Local
    // ReSharper disable UnusedParameter.Local
    private void EventsOnPropertyChange(int vmId, JavaObjectHandle evt, JavaObjectHandle source, string property, string oldValue, string newValue) {
      // Note: It seems this event is never fired. Maybe this is from older JDKs?
      LogNodeEvent(string.Format("PropertyChange: {0}", property),
        () => Tuple.Create(new AccessibleContextNode(_accessBridge, source), oldValue, newValue),
        () => ShowEventDialog(
          new AccessibleContextNode(_accessBridge, source),
          "Old value", oldValue,
          "New value", newValue));
    }

    private void EventsOnPropertyVisibleDataChange(int vmId, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("PropertyVisibleDataChange",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnPropertyValueChange(int vmId, JavaObjectHandle evt, JavaObjectHandle source, string oldValue, string newValue) {
      LogNodeEvent("PropertyVisibleDataChange",
        () => Tuple.Create(new AccessibleContextNode(_accessBridge, source), oldValue, newValue),
        () => ShowEventDialog(
          new AccessibleContextNode(_accessBridge, source),
          "Old value", oldValue,
          "New value", newValue));
    }

    private void EventsOnPropertyTextChange(int vmId, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("PropertyTextChange",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnPropertyStateChange(int vmId, JavaObjectHandle evt, JavaObjectHandle source, string oldState, string newState) {
      LogNodeEvent("PropertyStateChange",
        () => Tuple.Create(new AccessibleContextNode(_accessBridge, source), oldState, newState),
        () => ShowEventDialog(
          new AccessibleContextNode(_accessBridge, source),
          "Old state", oldState,
          "New state", newState));
    }

    private void EventsOnPropertySelectionChange(int vmId, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("PropertySelectionChange",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnPropertyNameChange(int vmId, JavaObjectHandle evt, JavaObjectHandle source, string oldName, string newName) {
      LogNodeEvent("PropertyNameChange",
        () => Tuple.Create(new AccessibleContextNode(_accessBridge, source), oldName, newName),
        () => ShowEventDialog(
          new AccessibleContextNode(_accessBridge, source),
          "Old name", oldName,
          "New name", newName));
    }

    private void EventsOnPropertyDescriptionChange(int vmId, JavaObjectHandle evt, JavaObjectHandle source, string oldDescription, string newDescription) {
      LogNodeEvent("PropertyDescriptionChange",
        () => Tuple.Create(new AccessibleContextNode(_accessBridge, source), oldDescription, newDescription),
        () => ShowEventDialog(
          new AccessibleContextNode(_accessBridge, source),
          "Old description", oldDescription,
          "New description", newDescription));
    }

    private void EventsOnPropertyCaretChange(int vmId, JavaObjectHandle evt, JavaObjectHandle source, int oldPosition, int newPosition) {
      LogNodeEvent("PropertyCaretChange",
        () => Tuple.Create(new AccessibleContextNode(_accessBridge, source), oldPosition.ToString(), newPosition.ToString()),
        () => ShowEventDialog(
          new AccessibleContextNode(_accessBridge, source),
          "Old position", oldPosition.ToString(),
          "New position", newPosition.ToString()));
    }

    private void EventsOnPropertyActiveDescendentChange(int vmId, JavaObjectHandle evt, JavaObjectHandle source, JavaObjectHandle oldActiveDescendent, JavaObjectHandle newActiveDescendent) {
      LogNodeEvent("PropertyActiveDescendentChange",
        () => Tuple.Create(
          new AccessibleContextNode(_accessBridge, source),
          new AccessibleContextNode(_accessBridge, oldActiveDescendent).GetTitle(),
          new AccessibleContextNode(_accessBridge, newActiveDescendent).GetTitle()),
        () => {
          ShowEventDialog(
            new AccessibleContextNode(_accessBridge, source),
            new AccessibleContextNode(_accessBridge, oldActiveDescendent),
            new AccessibleContextNode(_accessBridge, newActiveDescendent));
        });
    }

    private void EventsOnPropertyChildChange(int vmId, JavaObjectHandle evt, JavaObjectHandle source, JavaObjectHandle oldChild, JavaObjectHandle newChild) {
      LogNodeEvent("PropertyChildChange",
        () => Tuple.Create(
          new AccessibleContextNode(_accessBridge, source),
          new AccessibleContextNode(_accessBridge, oldChild).GetTitle(),
          new AccessibleContextNode(_accessBridge, newChild).GetTitle()),
        () => ShowEventDialog(
          new AccessibleContextNode(_accessBridge, source),
          new AccessibleContextNode(_accessBridge, oldChild),
          new AccessibleContextNode(_accessBridge, newChild)));
    }

    private void EventsOnMouseReleased(int vmId, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("MouseReleased",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnMousePressed(int vmId, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("MousePressed",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnMouseExited(int vmId, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("MouseExited",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnMouseEntered(int vmId, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("MouseEntered",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnMouseClicked(int vmId, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("MouseClicked",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnCaretUpdate(int vmId, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("CaretUpdate",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnFocusGained(int vmId, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("FocusGained",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnFocusLost(int vmId, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("FocusLost",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnJavaShutdown(int jvmId) {
      LogMessage("JVM {0} has shutdown. Refresh tree.", jvmId);
    }

    private void EventsOnMenuCanceled(int vmid, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("MenuCanceled",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnMenuDeselected(int vmid, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("MenuDeselected",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnMenuSelected(int vmid, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("MenuSelected",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnPopupMenuCanceled(int vmid, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("PopupMenuCanceled",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnPopupMenuWillBecomeInvisible(int vmid, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("PopupMenuWillBecomeInvisible",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnPopupMenuWillBecomeVisible(int vmid, JavaObjectHandle evt, JavaObjectHandle source) {
      LogNodeEvent("PopupMenuWillBecomeVisible",
        () => new AccessibleContextNode(_accessBridge, source),
        () => ShowEventDialog(new AccessibleContextNode(_accessBridge, source)));
    }

    private void EventsOnPropertyTableModelChange(int vmid, JavaObjectHandle evt, JavaObjectHandle source, string oldValue, string newValue) {
      LogNodeEvent("PropertyTableModelChange",
        () => Tuple.Create(new AccessibleContextNode(_accessBridge, source), oldValue, newValue),
        () => ShowEventDialog(
          new AccessibleContextNode(_accessBridge, source),
          "Old value", oldValue,
          "New value", newValue));
    }
    // ReSharper restore UnusedParameter.Local
    // ReSharper restore UnusedMember.Local
    #endregion
  }
}
