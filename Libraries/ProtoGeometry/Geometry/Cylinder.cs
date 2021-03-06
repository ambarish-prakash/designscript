﻿using Autodesk.DesignScript.Interfaces;

namespace Autodesk.DesignScript.Geometry
{
    public class Cylinder: Solid
    {
        internal IConeEntity CylinderEntity { get { return HostImpl as IConeEntity; } }

        #region INTERNAL_METHODS

        private void InitializeGuaranteedProperties()
        {
        }

        #endregion

        #region PRIVATE_CONSTRUCTORS

        static void InitType()
        {
            RegisterHostType(typeof(IConeEntity), CreateGeometry);
        }

        static Solid CreateGeometry(IGeometryEntity host, bool persist)
        {
            IConeEntity cone = host as IConeEntity;
            if (null == cone)
                return null;

            if (cone.Owner != null)
                return cone.Owner as Solid;

            if (IsCylinder(cone))
                return new Cylinder(cone, persist);

            return Cone.ToCone(cone, persist);
        }

        static bool IsCylinder(IConeEntity cone)
        {
            return cone.GetRadiusRatio().EqualsTo(1.0);
        }

        private Cylinder(IConeEntity entity, bool persist = false)
            : base(entity, persist) 
        {
            InitializeGuaranteedProperties();
        }
        #endregion

        #region PROTECTED_CONSTRUCTORS

        protected Cylinder(CoordinateSystem contextCoordinateSystem, double radius, double height,bool persist)
            : base(CylinderByRadiusHeightCore(contextCoordinateSystem.CSEntity, radius, height),persist)
        {            
            InitializeGuaranteedProperties();
            ContextCoordinateSystem = contextCoordinateSystem;
            Radius = radius;
            Height = height;
        }

        #endregion

        #region DESIGNSCRIPT_CONSTRUCTORS

        /// <summary>
        /// Constructs a solid cylinder defined by a line the start and end radii of its base and top respectively.
        /// </summary>
        /// <param name="contextCoordinateSystem">The parent CoordinateSystem of the cylinder</param>
        /// <param name="radius">The radius of the cylinder</param>
        /// <param name="height">The height of the cylinder</param>
        /// <returns></returns>
        public static Cylinder ByRadiusHeight(CoordinateSystem contextCoordinateSystem, double radius, double height)
        {
             return new Cylinder(contextCoordinateSystem,radius,height,true);
        }

        #endregion    
  
        #region CORE_METHODS

        private static IConeEntity CylinderByRadiusHeightCore(ICoordinateSystemEntity contextCoordinateSystem, double radius, double height)
        {
            string kMethod = "Cylinder.ByRadiusHeight";

            if (radius.LessThanOrEqualTo(0.0))
                throw new System.ArgumentException(string.Format(Properties.Resources.LessThanZero, "radius"), "radius");
            if (height.LessThanOrEqualTo(0.0))
                throw new System.ArgumentException(string.Format(Properties.Resources.LessThanZero, "height"), "height");
            if (null == contextCoordinateSystem)
                throw new System.ArgumentNullException("contextCoordinateSystem");
            if (!contextCoordinateSystem.IsUniscaledOrtho())
            {
                if(contextCoordinateSystem.IsScaledOrtho())
                    throw new System.ArgumentException(string.Format(Properties.Resources.InvalidInput, "Non Uniform Scaled CoordinateSystem", kMethod));
                else
                    throw new System.ArgumentException(string.Format(Properties.Resources.InvalidInput, "Shear CoordinateSystem", kMethod));
            }

            IConeEntity entity = HostFactory.Factory.ConeByRadiusLength(contextCoordinateSystem,
                                                                        radius, radius, height);
            if (null == entity)
                throw new System.Exception(string.Format(Properties.Resources.OperationFailed, kMethod));
            return entity;
        }

        #endregion

        #region PROPERTIES

        private double? radius;
        /// <summary>
        /// 
        /// </summary>
        public double Radius 
        { 
            get
            {
                if (!radius.HasValue)
                {
                    radius = CylinderEntity.GetStartRadius();
                }
                return radius.Value;
            }
            private set
            {
                radius = value;
            }
        }

        private double? height;
        /// <summary>
        /// 
        /// </summary>
        public double Height 
        {
            get
            {
                if (!height.HasValue)
                {
                    height = CylinderEntity.GetHeight();
                }
                return height.Value;
            }
            private set
            {
                height = value;
            }
        }

        #endregion
    }
}
