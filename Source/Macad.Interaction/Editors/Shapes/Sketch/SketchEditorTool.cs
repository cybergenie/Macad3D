﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Macad.Interaction.Visual;
using Macad.Common;
using Macad.Common.Serialization;
using Macad.Core;
using Macad.Core.Shapes;
using Macad.Core.Topology;
using Macad.Occt;

namespace Macad.Interaction.Editors.Shapes
{
    public sealed class SketchEditorTool : Tool
    {
        #region Constants

        const string UnselectedStatusText = "";
        const string MixedSelectedStatusText = "Multiple items selected.";
        const string SegmentSelectedStatusText = "Segment {0} selected.";
        const string MultiSegmentSelectedStatusText = "Multiple Segments ({0}) selected.";
        const string PointSelectedStatusText = "Point {0} selected.";
        const string MultiPointSelectedStatusText = "Multiple Points ({0}) selected.";
        const string ConstraintSelectedStatusText = "Constraint {0} selected.";
        const string MultiConstraintSelectedStatusText = "Multiple Constraints ({0}) selected.";

        #endregion

        #region Public Properties

        public SketchTool CurrentTool
        {
            get { return _CurrentTool; }
            set
            {
                _CurrentTool = value;
                RaisePropertyChanged();
            }
        }

        //--------------------------------------------------------------------------------------------------

        public bool ContinuesSegmentCreation { get { return _ContinuesSegmentCreation; } }

        public Sketch Sketch { get; private set; }
        public Trsf Transform { get; private set; } = Trsf.Identity;

        public SketchEditorElements Elements { get; }

        //--------------------------------------------------------------------------------------------------

        public List<int> SelectedSegmentIndices { get; private set; }
        public List<SketchSegment> SelectedSegments
        {
            get { return _SelectedSegments; }
            private set
            {
                _SelectedSegments = value;
                RaisePropertyChanged();
            }
        }

        //--------------------------------------------------------------------------------------------------

        public List<SketchConstraint> SelectedConstraints
        {
            get { return _SelectedConstraints; }
            private set
            {
                _SelectedConstraints = value;
                RaisePropertyChanged();
            }
        }

        //--------------------------------------------------------------------------------------------------

        public List<int> SelectedPoints
        {
            get { return _SelectedPoints; }
            private set
            {
                _SelectedPoints = value;
                RaisePropertyChanged();
            }
        }

        //--------------------------------------------------------------------------------------------------

        public bool ClipPlaneEnabled
        {
            get { return _ClipPlane != null; }
        }

        //--------------------------------------------------------------------------------------------------

        public double ViewRotation { get; private set; }

        //--------------------------------------------------------------------------------------------------

        #endregion

        #region Member

        Dictionary<int, Pnt2d> _TempPoints;
        double _LastGizmoScale;
        double _LastPixelSize;
        double[] _SavedViewParameters;
        ClipPlane _ClipPlane;

        SelectSketchElementAction _SelectAction;
        MoveSketchPointAction _MoveAction;
        SketchTool _CurrentTool;
        bool _ContinuesSegmentCreation;
        List<int> _SelectedPoints;
        List<SketchSegment> _SelectedSegments;
        List<SketchConstraint> _SelectedConstraints;

        //--------------------------------------------------------------------------------------------------

        #endregion

        #region Tool Implementation

        public SketchEditorTool(Sketch sketch)
        {
            Sketch = sketch;
            Debug.Assert(Sketch != null);
            Elements = new SketchEditorElements(this);
        }

        //--------------------------------------------------------------------------------------------------

        protected override bool OnStart()
        {
            OpenSelectionContext(SelectionContext.Options.NewSelectedList);
            var workspace = WorkspaceController.Workspace;

            var editorSettings = SketchEditorSettingsCache.GetOrCreate(Sketch);

            editorSettings.WorkingContext ??= workspace.WorkingContext.Clone();
            workspace.WorkingContext = editorSettings.WorkingContext;
            WorkspaceController.LockWorkingPlane = true;
            workspace.WorkingPlane = Sketch.Plane;

            var vc = WorkspaceController.ActiveViewControlller;
            _SavedViewParameters = vc.Viewport.GetViewParameters();
            vc.LockedToPlane = true;
            
            if (editorSettings.ViewParameters != null)
            {
                vc.Viewport.RestoreViewParameters(editorSettings.ViewParameters, Sketch.GetTransformation());
                // Update direction
                vc.SetPredefinedView(ViewportController.PredefinedViews.WorkingPlane);
                RotateView(editorSettings.ViewRotation);
            } else {
                _CenterView();
            }
            vc.Viewport.PropertyChanged += _Viewport_PropertyChanged;

            EnableClipPlane(editorSettings.ClipPlaneEnabled);

            if (Sketch.IsVisible)
            {
                var visualSketchShape = WorkspaceController.VisualObjects.Get(Sketch.Body, true) as VisualShape;
                if (visualSketchShape != null)
                    visualSketchShape.IsHidden = true;
            }

            _LastGizmoScale = WorkspaceController.ActiveViewport.GizmoScale;

            _TempPoints = new Dictionary<int, Pnt2d>(Sketch.Points);
            Transform = Sketch.GetTransformation();

            Elements.InitElements(_TempPoints);
            _UpdateSelections();

            Sketch.ElementsChanged += _Sketch_ElementsChanged;

            _SelectAction = new SelectSketchElementAction(this);
            if (!StartAction(_SelectAction))
                return false;
            _SelectAction.Finished += _SelectAction_Finished;

            SetHintMessage(UnselectedStatusText);

            return true;
        }

        //--------------------------------------------------------------------------------------------------

        protected override void OnStop()
        {
            if (CurrentTool != null)
            {
                StopTool();
            }

            if (Sketch != null)
            {
                if (Sketch.IsVisible)
                {
                    var visualSketchShape = WorkspaceController.VisualObjects.Get(Sketch.Body) as VisualShape;
                    if (visualSketchShape != null)
                        visualSketchShape.IsHidden = false;
                }

                Sketch.ElementsChanged -= _Sketch_ElementsChanged;
            }

            Elements.RemoveAll();

            WorkspaceController.Workspace.WorkingContext = null;
            WorkspaceController.LockWorkingPlane = false;

            var vc = WorkspaceController.ActiveViewControlller;
            if (Sketch != null)
            {
                var editorSettings = SketchEditorSettingsCache.GetOrCreate(Sketch);
                editorSettings.ViewParameters = vc.Viewport.GetViewParameters(Sketch.GetTransformation().Inverted());
                editorSettings.ViewRotation = ViewRotation;
                editorSettings.ClipPlaneEnabled = ClipPlaneEnabled;
            }

            vc.Viewport.PropertyChanged -= _Viewport_PropertyChanged;
            vc.LockedToPlane = false;
            if (_SavedViewParameters != null)
            {
                vc.Viewport.RestoreViewParameters(_SavedViewParameters);
            }

            EnableClipPlane(false);

            WorkspaceController.Invalidate();
        }

        //--------------------------------------------------------------------------------------------------

        protected override bool OnCancel()
        {
            if (CurrentTool != null)
            {
                StopTool();
                return false;
            }

            return true;
        }

        //--------------------------------------------------------------------------------------------------

        public override bool PrepareUndo()
        {
            // override base behaviour of closing our tool
            return true;
        }

        //--------------------------------------------------------------------------------------------------
        
        void _Viewport_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Viewport.PixelSize))
            {
                Elements.OnGizmoScaleChanged(_TempPoints, Sketch.Segments);
            }
        }

        //--------------------------------------------------------------------------------------------------

        public override bool OnMouseMove(MouseEventData data)
        {
            // Check if we need to scale the trihedron
            if (!_LastGizmoScale.IsEqual(WorkspaceController.ActiveViewport.GizmoScale, 0.001)
                || !_LastPixelSize.IsEqual(WorkspaceController.ActiveViewport.PixelSize, 0.001))
            {
                _LastGizmoScale = WorkspaceController.ActiveViewport.GizmoScale;
                _LastPixelSize = WorkspaceController.ActiveViewport.PixelSize;
                //Debug.WriteLine("Gizmo scaled, last is " + _LastGizmoScale);
                Elements.OnGizmoScaleChanged(_TempPoints, Sketch.Segments);
                data.ForceReDetection = true;
            }

            return base.OnMouseMove(data);
        }

        //--------------------------------------------------------------------------------------------------

        public void RotateView(double degree)
        {
            ViewRotation = Maths.NormalizeAngleDegree(ViewRotation + degree);
            double twist = Maths.NormalizeAngleDegree(WorkspaceController.ActiveViewport.Twist + degree);
            WorkspaceController.ActiveViewport.Twist = twist;
            Elements.OnViewRotated(_TempPoints, Sketch.Segments);
            WorkspaceController.Invalidate();
        }

        //--------------------------------------------------------------------------------------------------

        void _CenterView()
        {
            // Set look at point
            var centerPnt = Sketch.GetTransformation().TranslationPart();
            WorkspaceController.ActiveViewport.TargetPoint = centerPnt.ToPnt();

            // Update direction
            WorkspaceController.ActiveViewControlller.SetPredefinedView(ViewportController.PredefinedViews.WorkingPlane);
        }

        //--------------------------------------------------------------------------------------------------
        
        public void EnableClipPlane(bool enable)
        {
            if (enable)
            {
                if (_ClipPlane != null)
                    return;
                
                var sketchPlane = Sketch.Plane;
                var reversedPlane = new Pln(new Ax3(sketchPlane.Location, sketchPlane.Axis.Direction.Reversed()));
                reversedPlane.Translate(reversedPlane.Axis.Direction.ToVec(-0.000001));

                _ClipPlane = new ClipPlane(reversedPlane);
                _ClipPlane.AddViewport(WorkspaceController.ActiveViewport);
                WorkspaceController.Invalidate();
                RaisePropertyChanged(nameof(ClipPlaneEnabled));
            }
            else
            {
                if (_ClipPlane == null)
                    return;
                
                _ClipPlane.Remove();
                _ClipPlane = null;
                WorkspaceController.Invalidate();
                RaisePropertyChanged(nameof(ClipPlaneEnabled));
            }
        }

        //--------------------------------------------------------------------------------------------------

        public override void EnrichContextMenu(ContextMenuItems itemList)
        {
            itemList.AddCommand(SketchCommands.CloseSketchEditor, null);

            itemList.AddGroup("Create Segment");
            itemList.AddCommand(SketchCommands.CreateSegment, SketchCommands.Segments.Line);
            itemList.AddCommand(SketchCommands.CreateSegment, SketchCommands.Segments.PolyLine);
            itemList.AddCommand(SketchCommands.CreateSegment, SketchCommands.Segments.Bezier2);
            itemList.AddCommand(SketchCommands.CreateSegment, SketchCommands.Segments.Bezier3);
            itemList.AddCommand(SketchCommands.CreateSegment, SketchCommands.Segments.Circle);
            itemList.AddCommand(SketchCommands.CreateSegment, SketchCommands.Segments.ArcCenter);
            itemList.AddCommand(SketchCommands.CreateSegment, SketchCommands.Segments.ArcRim);
            itemList.AddCommand(SketchCommands.CreateSegment, SketchCommands.Segments.EllipseCenter);
            itemList.AddCommand(SketchCommands.CreateSegment, SketchCommands.Segments.EllipticalArcCenter);
            itemList.AddCommand(SketchCommands.CreateSegment, SketchCommands.Segments.Rectangle);
            itemList.CloseGroup();

            itemList.AddGroup("Create Constraint");
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.Fixed);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.LineLength);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.Angle);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.CircleRadius);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.Equal);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.Perpendicular);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.Parallel);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.Concentric);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.Horizontal);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.Vertical);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.HorizontalDistance);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.VerticalDistance);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.Tangent);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.PointOnSegment);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.PointOnMidpoint);
            itemList.AddCommandIfExecutable(SketchCommands.CreateConstraint, SketchCommands.Constraints.SmoothCorner);
            itemList.CloseGroup();

            itemList.AddCommand(SketchCommands.SplitElement);
            itemList.AddCommand(SketchCommands.WeldElements);

            itemList.AddGroup("Convert Segment");
            itemList.AddCommandIfExecutable(SketchCommands.ConvertSegment, SketchCommands.Segments.Line);
            itemList.AddCommandIfExecutable(SketchCommands.ConvertSegment, SketchCommands.Segments.Bezier);
            itemList.AddCommandIfExecutable(SketchCommands.ConvertSegment, SketchCommands.Segments.Arc);
            itemList.AddCommandIfExecutable(SketchCommands.ConvertSegment, SketchCommands.Segments.EllipticalArc);
            itemList.CloseGroup();

            itemList.AddCommand(SketchCommands.RecenterGrid);
        }

        //--------------------------------------------------------------------------------------------------

        #endregion

        #region Selection

        void _SelectAction_Finished(ToolAction toolAction)
        {
            _OnSelectionChanged();
        }

        //--------------------------------------------------------------------------------------------------

        void _OnSelectionChanged()
        {
            if (CurrentTool != null)
                return;

            if (_MoveAction != null)
            {
                StopAction(_MoveAction);
                _MoveAction = null;
            }

            _UpdateSelections();

            if (SelectedSegments.Any() || SelectedPoints.Any())
            {
                _MoveAction = new MoveSketchPointAction(this);
                if (!StartAction(_MoveAction, false))
                    return;
                _MoveAction.Preview += _MoveAction_Preview;
                _MoveAction.Finished += _MoveAction_Finished;

                var segPoints = SelectedSegments.SelectMany(seg => seg.Points);
                _MoveAction.SetSketchElements(Sketch, SelectedPoints.Union(segPoints).ToList());
            }

            WorkspaceController.Invalidate();
        }

        //--------------------------------------------------------------------------------------------------

        void _UpdateSelections()
        {
            SelectedPoints = Elements.GetSelectedPointIndices().ToList();
            SelectedSegments = Elements.GetSelectedSegments().ToList();
            SelectedSegmentIndices = Elements.GetSelectedSegmentIndices().ToList();
            SelectedConstraints = Elements.GetSelectedConstraints().ToList();
            _UpdateStatusText();
        }

        //--------------------------------------------------------------------------------------------------

        public void Select(IEnumerable<int> pointIndices, IEnumerable<int> segmentIndices)
        {
            Elements.Select(pointIndices, segmentIndices);
            _OnSelectionChanged();
        }

        //--------------------------------------------------------------------------------------------------

        void _UpdateStatusText()
        {

            if (SelectedSegments.Any() && !SelectedPoints.Any() && !SelectedConstraints.Any())
            {
                SetHintMessage(SelectedSegments.Count == 1 
                                   ? string.Format(SegmentSelectedStatusText, SelectedSegments[0].GetType().Name) 
                                   : string.Format(MultiSegmentSelectedStatusText, SelectedSegments.Count));
            }
            else if (!SelectedSegments.Any() && SelectedPoints.Any() && !SelectedConstraints.Any())
            {
                SetHintMessage(SelectedPoints.Count == 1 
                                   ? string.Format(PointSelectedStatusText, SelectedPoints[0]) 
                                   : string.Format(MultiPointSelectedStatusText, SelectedPoints.Count));
            }
            else if (!SelectedSegments.Any() && !SelectedPoints.Any() && SelectedConstraints.Any())
            {
                SetHintMessage(SelectedConstraints.Count == 1 
                                   ? string.Format(ConstraintSelectedStatusText, SelectedConstraints[0].GetType().Name) 
                                   : string.Format(MultiConstraintSelectedStatusText, SelectedConstraints.Count));
            }
            else if (SelectedSegments.Any() || SelectedPoints.Any() || SelectedConstraints.Any())
            {
                SetHintMessage(MixedSelectedStatusText);
            }
            else
            {
                SetHintMessage(UnselectedStatusText);
            }
        }

        //--------------------------------------------------------------------------------------------------

        void _Sketch_ElementsChanged(Sketch sketch, Sketch.ElementType types)
        {
            if (sketch != Sketch)
                return;

            // Check for new ones
            if (types.HasFlag(Sketch.ElementType.Point))
            {
                // Take over new points
                _TempPoints = new Dictionary<int, Pnt2d>(Sketch.Points);
            }

            Elements.OnSketchChanged(Sketch, types);
            _UpdateSelections();
        }

        //--------------------------------------------------------------------------------------------------

        #endregion

        #region Movement

        bool _ApplyMoveActionDelta(MoveSketchPointAction.EventArgs args)
        {
            if (_MoveAction.IsMoving)
            {
                foreach (var pointIndex in args.Points)
                {
                    _TempPoints[pointIndex] = Sketch.Points[pointIndex].Translated(args.MoveDelta);
                }
            }
            else if (_MoveAction.IsRotating)
            {
                foreach (var pointIndex in args.Points)
                {
                    _TempPoints[pointIndex] = Sketch.Points[pointIndex].Rotated(args.RotateCenter, args.RotateDelta);
                }
            }
            else
            {
                // No movement
                return false;
            }

            SketchConstraintSolver.Solve(Sketch, _TempPoints, false);
            Elements.OnPointsChanged(_TempPoints, Sketch.Segments);
            WorkspaceController.Invalidate();
            return true;
        }

        //--------------------------------------------------------------------------------------------------

        void _MoveAction_Preview(MoveSketchPointAction sender, MoveSketchPointAction.EventArgs args)
        {
            if (CurrentTool != null)
                return;

            _ApplyMoveActionDelta(args);
        }

        //--------------------------------------------------------------------------------------------------

        void _MoveAction_Finished(MoveSketchPointAction sender, MoveSketchPointAction.EventArgs args)
        {
            if (CurrentTool != null)
                return;

            if (!args.Points.Any())
            {
                // MoveAction has lost all it's point, stop it now
                StopAction(_MoveAction);
                _MoveAction = null;
                WorkspaceController.Invalidate();
                return;
            }

            if (_ApplyMoveActionDelta(args))
            {
                Sketch.Points = new Dictionary<int, Pnt2d>(_TempPoints);

                // Check if points can be merged, and merge them
                var mergeCandidates = _MoveAction.CheckMergePoints(false);

                if (mergeCandidates.Any())
                {
                    var pointString = mergeCandidates.Select(mc => $"({mc.Value},{mc.Key})").ToList().Join(", ");
                    if (Dialogs.Dialogs.AskSketchPointMerge(pointString))
                    {
                        // Do merge
                        foreach (var mc in mergeCandidates)
                        {
                            Sketch.MergePoints(mc.Value, mc.Key);
                        }
                    }
                }

                // Run solver 
                Sketch.SolveConstraints(true);

                // Commit changes
                CommitChanges();
            }
        }

        //--------------------------------------------------------------------------------------------------

        #endregion

        #region Segment Creation

        public bool StartSegmentCreation<T>(bool continuesCreation = false) where T : SketchSegmentCreator, new()
        {
            Select(null, null);
            StopTool();

            T newCreator = new T();

            if (!newCreator.Start(this))
                return false;

            _ContinuesSegmentCreation = continuesCreation;
            CurrentTool = newCreator;
            return true;
        }

        //--------------------------------------------------------------------------------------------------

        public void FinishSegmentCreation(Dictionary<int, Pnt2d> points, int[] mergePointIndices, IEnumerable<SketchSegment> segments, IEnumerable<SketchConstraint> constraints, int continueWithPoint = -1)
        {
            var (pointMap, segmentMap, _) = Sketch.AddElements(points, mergePointIndices, segments.ToIndexedDictionary(), constraints);
            CommitChanges();
            WorkspaceController.UpdateSelection();

            if (_ContinuesSegmentCreation && continueWithPoint >= 0 && pointMap.ContainsKey(continueWithPoint))
            {
                (CurrentTool as SketchSegmentCreator)?.Continue(pointMap[continueWithPoint]);
            }
            else
            {
                StopTool();
                Elements.DeselectAll();
                Elements.Select(null, segmentMap.Values);
                _OnSelectionChanged();
            }
        }

        //--------------------------------------------------------------------------------------------------

        #endregion

        #region Constraint Creation

        public bool CanCreateConstraint<T>() where T : SketchConstraint
        {
            // Don't allow constraint creation if any constraint is selected
            if (SelectedConstraints != null && SelectedConstraints.Any())
                return false;

            return SketchConstraintCreator.CanCreate<T>(Sketch, SelectedPoints, SelectedSegmentIndices);
        }

        //--------------------------------------------------------------------------------------------------

        public List<SketchConstraint> CreateConstraint<T>() where T : SketchConstraint
        {
            if (!CanCreateConstraint<T>())
                return null;

            var constraints = SketchConstraintCreator.Create<T>(Sketch, SelectedPoints, SelectedSegmentIndices);
            if (constraints != null)
            {
                Sketch.AddElements(null, null, null, constraints);
                Sketch.SolveConstraints(false);
                Sketch.SolveConstraints(true);
                CommitChanges();

                StopAction(_MoveAction);
                Elements.DeselectAll();
                Elements.Select(constraints);
                _UpdateSelections();
            }

            return constraints;
        }

        //--------------------------------------------------------------------------------------------------

        #endregion

        #region Delete, Duplicate, Clipboard

        const string ClipboardContentFormat = "Macad.SketchContent.1";

        [SerializeType]
        class SketchCloneContent
        {
            [SerializeMember]
            internal Dictionary<int, Pnt2d> Points { get; init; }
            [SerializeMember]
            internal Dictionary<int, SketchSegment> Segments { get; init; }
            [SerializeMember]
            internal SketchConstraint[] Constraints { get; init; }
        }

        //--------------------------------------------------------------------------------------------------

        public override bool CanDelete()
        {
            return SelectedPoints?.Count > 0 || SelectedSegments?.Count > 0 || SelectedConstraints?.Count > 0;
        }

        //--------------------------------------------------------------------------------------------------

        public override void Delete()
        {
            SelectedConstraints.ForEach(c => Sketch.DeleteConstraint(c));
            SelectedSegments.ForEach(s => Sketch.DeleteSegment(s));
            SelectedPoints.ForEach(p => SketchUtils.DeletePointTrySubstituteSegments(Sketch, p));

            Sketch.SolveConstraints(true);
            CommitChanges();

            Select(null, null);
        }

        //--------------------------------------------------------------------------------------------------

        public override bool CanDuplicate()
        {
            return SelectedSegments?.Count > 0;
        }

        //--------------------------------------------------------------------------------------------------

        SketchCloneContent _CreateCloneContentFromSelection()
        {
            var segmentDict = SelectedSegmentIndices.ToDictionary(index => index, index => Sketch.Segments[index]);
            var pointIndices = SelectedSegments.SelectMany(seg => seg.Points).Distinct().ToArray();
            var pointDict = pointIndices.ToDictionary(index => index, index => Sketch.Points[index]);
            var constraints = Sketch.Constraints.Where(constraint =>
                                                           (constraint.Points == null || pointIndices.ContainsAll(constraint.Points))
                                                           && (constraint.Segments == null || SelectedSegmentIndices.ContainsAll(constraint.Segments))).ToArray();

            return new SketchCloneContent
            {
                Points = pointDict,
                Segments = segmentDict,
                Constraints = constraints
            };
        }

        //--------------------------------------------------------------------------------------------------

        void _ApplyCloneContent(in SketchCloneContent content)
        {
            var (pointMap, segmentMap, _) = Sketch.AddElements(content.Points, null, content.Segments, content.Constraints);
            Sketch.SolveConstraints(true);
            CommitChanges();

            Select(pointMap.Values.Distinct(), segmentMap.Values.Distinct());
        }

        //--------------------------------------------------------------------------------------------------

        public override void Duplicate()
        {
            if (!CanDuplicate())
                return;

            var content = _CreateCloneContentFromSelection();
            var serialized = Serializer.Serialize(content, new SerializationContext(SerializationScope.CopyPaste));
            if (serialized.IsNullOrEmpty())
                return;

            var copiedContent = Serializer.Deserialize<SketchCloneContent>(serialized, new SerializationContext(SerializationScope.CopyPaste));
            _ApplyCloneContent(copiedContent);
        }

        //--------------------------------------------------------------------------------------------------

        public override bool CanCopyToClipboard()
        {
            return CanDuplicate();
        }

        //--------------------------------------------------------------------------------------------------

        public override void CopyToClipboard()
        {
            if (!CanCopyToClipboard())
                return;

            var content = _CreateCloneContentFromSelection();
            var serialized = Serializer.Serialize(content, new SerializationContext(SerializationScope.CopyPaste));
            if (serialized.IsNullOrEmpty())
                return;

            Core.Clipboard.Current?.SetData(ClipboardContentFormat, serialized);
        }

        //--------------------------------------------------------------------------------------------------

        public override bool CanPasteFromClipboard()
        {
            return Core.Clipboard.Current?.ContainsData(ClipboardContentFormat) ?? false;
        }

        //--------------------------------------------------------------------------------------------------

        public override IEnumerable<InteractiveEntity> PasteFromClipboard()
        {
            var serialized = Core.Clipboard.Current?.GetDataAsString(ClipboardContentFormat);
            if (serialized == null)
                return null;

            var copiedContent = Serializer.Deserialize<SketchCloneContent>(serialized, new SerializationContext(SerializationScope.CopyPaste));
            _ApplyCloneContent(copiedContent);
            return null;
        }

        //--------------------------------------------------------------------------------------------------

        #endregion

        #region Tools

        public void StartTool(SketchTool tool)
        {
            StopTool();

            if (tool.Start(this))
            {
                _MoveAction?.Stop();
                _MoveAction = null;
                CurrentTool = tool;
            }
        }

        //--------------------------------------------------------------------------------------------------

        public void StopTool()
        {
            CurrentTool?.Stop();
            CurrentTool = null;
            _ContinuesSegmentCreation = false;

            // Update activation, since we have switched the local context
            Elements.Activate(true, true, true);
            _OnSelectionChanged();
            _UpdateStatusText();
        }

        //--------------------------------------------------------------------------------------------------
        
        internal bool StartToolAction(ToolAction toolAction)
        {
            // Forward for sketch tool
            return StartAction(toolAction, false);
        }
        
        //--------------------------------------------------------------------------------------------------
        
        internal void StopToolAction(ToolAction toolAction)
        {
            // Forward for sketch tool
            StopAction(toolAction);
        }

        //--------------------------------------------------------------------------------------------------

        #endregion

    }
}
