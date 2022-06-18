﻿using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Macad.Common;
using Macad.Core;
using Macad.Core.Shapes;
using Macad.Core.Topology;
using Macad.Interaction.Panels;
using Macad.Occt;
using Macad.Presentation;

namespace Macad.Interaction.Editors.Shapes
{
    public partial class CrossSectionPropertyPanel : PropertyPanel
    {
        public CrossSection CrossSection { get; private set; }

        //--------------------------------------------------------------------------------------------------

        public double Offset
        {
            get { return _Offset; }
            set
            {
                if (_Offset != value)
                {
                    _Offset = value;
                    RaisePropertyChanged();
                    _RecalcPlane();
                }
            }
        }

        double _Offset;

        //--------------------------------------------------------------------------------------------------

        public double RotationPitch
        {
            get { return _RotationPitch; }
            set
            {
                if (_RotationPitch != value)
                {
                    _RotationPitch = value;
                    RaisePropertyChanged();
                    _RecalcPlane();
                }
            }
        }

        double _RotationPitch;

        //--------------------------------------------------------------------------------------------------

        public double RotationRoll
        {
            get { return _RotationRoll; }
            set
            {
                if (_RotationRoll != value)
                {
                    _RotationRoll = value;
                    RaisePropertyChanged();
                    _RecalcPlane();
                }
            }
        }

        double _RotationRoll;
        Pln _TranslatedPlane;

        //--------------------------------------------------------------------------------------------------

        void _RecalcPlane()
        {
            var euler = CrossSection.Plane.Rotation().ToEuler();
            Quaternion rotation = new Quaternion(euler.yaw, _RotationPitch.ToRad(), _RotationRoll.ToRad());
            var loc = new Pnt(0, 0, _Offset);
            CrossSection.Plane = new Pln(rotation.ToAx3(loc));
        }

        //--------------------------------------------------------------------------------------------------

        void _UpdateFromPlane()
        {
            var euler = CrossSection.Plane.Rotation().ToEuler();
            _RotationPitch = euler.pitch.ToDeg();
            _RotationRoll = euler.roll.ToDeg();
            RaisePropertyChanged(nameof(RotationPitch));
            RaisePropertyChanged(nameof(RotationRoll));

            _TranslatedPlane = CrossSection.GetCenteredPlane(out var _);
            _Offset = _TranslatedPlane.Location.Z;
            RaisePropertyChanged(nameof(Offset));
        }

        //--------------------------------------------------------------------------------------------------

        public ICommand SwitchFilterCommand { get; private set; }

        void ExecuteSwitchFilter(CrossSection.WireFilter filter)
        {
            if (CrossSection.Filter != filter)
            {
                CrossSection.Filter = filter;
                CommmitChange();
            }
        }

        //--------------------------------------------------------------------------------------------------

        public ICommand TakeWorkingPlaneCommand { get; private set; }

        void ExecuteTakeWorkingPlane()
        {
            CrossSection.Plane = WorkspaceController.Workspace.WorkingPlane
                                                              .Transformed(CrossSection.Body.GetTransformation()
                                                                                            .Inverted());
            CommmitChange();
        }

        //--------------------------------------------------------------------------------------------------

        public override void Initialize(BaseObject instance)
        {
            CrossSection = instance as CrossSection;
            Debug.Assert(CrossSection != null);

            SwitchFilterCommand = new RelayCommand<CrossSection.WireFilter>(ExecuteSwitchFilter);
            TakeWorkingPlaneCommand = new RelayCommand(ExecuteTakeWorkingPlane);

            CrossSection.PropertyChanged += _CrossSection_PropertyChanged;
            _UpdateFromPlane();

            InitializeComponent();
        }

        //--------------------------------------------------------------------------------------------------

        void _CrossSection_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CrossSection.Plane))
            {
                _UpdateFromPlane();
            }
        }

        //--------------------------------------------------------------------------------------------------

        public override void Cleanup()
        {
            CrossSection.PropertyChanged -= _CrossSection_PropertyChanged;
        }

        //--------------------------------------------------------------------------------------------------

    }
}

