﻿using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using KSP;
using SolverEngines;

namespace AJE
{
    public interface ITorqueProducer
    {
        /// <summary>
        /// Update engine conditions based on solver conditions.
        /// </summary>
        /// <param name="solver">pass 'this' when calling from solver</param>
        /// <param name="shaftRPM">rpm of the shaft the prop is on</param>
        /// <param name="deltaTime">the current delta time for updates</param>
        void Update(EngineSolver solver, double shaftRPM, double deltaTime);

        /// <summary>
        /// Power delivered to the propeller in W.
        /// </summary>
        /// <returns></returns>
        double GetShaftPower();

        /// <summary>
        /// Total power generated by the engine in W.
        /// </summary>
        /// <returns></returns>
        double GetTotalPower();

        /// <summary>
        /// Thrust in kN from the engine itself
        /// </summary>
        /// <returns></returns>
        double GetEngineThrust();

        /// <summary>
        /// Fuel flow in kg/sec.
        /// </summary>
        /// <returns></returns>
        double GetFuelFlow();

        /// <summary>
        /// Gets engine temperature in K
        /// </summary>
        /// <returns></returns>
        double GetTemp();

        /// <summary>
        /// Gets engine status.
        /// </summary>
        /// <returns></returns>
        string GetStatus();

        /// <summary>
        /// Gets the FX power of the engine (0-1)
        /// </summary>
        /// <returns></returns>
        float GetFXPower();

        /// <summary>
        /// Sets multiplier to engine power.
        /// </summary>
        /// <param name="newMult">new multiplier</param>
        void SetMultiplier(double newMult);
    }
    public class SolverPropeller : EngineSolver
    {
        /// <summary>
        /// The engine that produces the torque.
        /// </summary>
        protected ITorqueProducer engine = null;

        /// <summary>
        /// The propeller that converts torque to thrust
        /// </summary>
        protected AJEPropJSB propJSB = null;

        // prop stats
        protected double propRPM = 0d;
        protected double propThrust = 0d;
        protected double propPitch = 0d;
        protected double minRPM = 60d;
        protected double maxRPM = 60d;
        protected double maxRPMRecip = 1d / 60d;
        protected double diameter = 2d;
        protected double ixx = 0.1d;
        protected double rpmLever = 1d;

        // engine stats
        double maxPower = 1d;
        double BSFC = 5e-9;
        double shaftPower = 0d;
        double engineThrust = 0d;
        double temperature = 288d;
        float fxPower = 0f;
        double powerTweak = 1d;
        double gearRatio = 1d;
        double gearRatioRecip = 1d;
        bool combusting = false;

        public int GetConstantSpeed() { return propJSB.GetConstantSpeed(); }
        public double GetDiameter() { return diameter; }
        public double GetPropRPM() { return propRPM; }
        public double GetPropPitch() { return propPitch; }
        public double GetPropThrust() { return propThrust; }
        public double GetShaftPower() { return shaftPower; }

        public SolverPropeller(ITorqueProducer eng, double power, double sfc, double gear, string propName, double minR, double maxR, double diam, double ixx)
        {
            engine = eng;
            maxPower = power;
            BSFC = sfc;
            gearRatio = gear;
            gearRatioRecip = 1d / gear;
            propJSB = new AJEPropJSB(propName, minR, maxR, diam, ixx);

            // set our prop stats
            diameter = propJSB.GetDiameter();
            ixx = propJSB.GetIxx();
            minRPM = propJSB.GetMinRPM();
            maxRPM = propJSB.GetMaxRPM();
            maxRPMRecip = 1d / maxRPM;

        }

        public void SetTweaks(double CtTweak, double CpTweak, double VolETweak, double MachPowTweak)
        {
            propJSB.SetTweaks(CtTweak, CpTweak, MachPowTweak);
            powerTweak = VolETweak;
            if (engine != null)
                engine.SetMultiplier(powerTweak);
        }

        public void SetRPMLever(double val) { rpmLever = val; }

        public override void CalculatePerformance(double airRatio, double commandedThrottle, double flowMult, double ispMult)
        {
            base.CalculatePerformance(airRatio, commandedThrottle, flowMult, ispMult);

            shaftPower = 0d;
            engineThrust = 0d;
            double deltaTime = TimeWarp.fixedDeltaTime;
            if (engine == null)
            {
                if (ffFraction > 0d)
                {
                    combusting = true;
                    fxPower = (float)throttle;
                    shaftPower = maxPower * throttle * ispMult * flowMult;
                    fuelFlow = shaftPower * BSFC * flowMult;
                    statusString = "Nominal";
                }
                else
                {
                    combusting = false;
                    statusString = "No fuel";
                    fxPower = 0f;
                }
                if (shaftPower > 0d)
                {
                    double desiredTemp = t0 + 20d + throttle * 100d; // total hack
                    if (temperature > desiredTemp - 1d)
                        temperature = desiredTemp;
                    else
                        temperature += 20d * deltaTime;
                }
                else
                {
                    if (temperature < t0 + 1d)
                        temperature = t0;
                    else
                        temperature += (t0 - temperature) * 0.05d * deltaTime;
                }
            }
            else
            {
                engine.Update(this, propRPM * gearRatioRecip, deltaTime);
                shaftPower = engine.GetShaftPower() * ispMult * flowMult;
                fuelFlow = engine.GetFuelFlow() * flowMult;
                temperature = engine.GetTemp();
                combusting = fuelFlow > 0d;
                fxPower = engine.GetFXPower();
                engineThrust = engine.GetEngineThrust();
                statusString = engine.GetStatus();
            }

            // now handle propeller
            if (rho > 0d)
            {
                propJSB.SetAdvance(rpmLever);
                propThrust = propJSB.Calculate(shaftPower, rho, vel, eair0, TimeWarp.fixedDeltaTime);
                propRPM = propJSB.GetRPM();
                propPitch = (float)propJSB.GetPitch();

                // TODO: Assign thrustRot and thrustOffset based on PFactor calcs

                double tmpRatio = propRPM * maxRPMRecip;
                if (tmpRatio > 1d)
                    tmpRatio *= tmpRatio;
                // engine overspeed correction (internal friction at high RPM)
                if (tmpRatio > 1.1)
                    propRPM -= (tmpRatio * tmpRatio * tmpRatio) * maxRPM * deltaTime;

                thrust += propThrust;
            }
            if (fuelFlow > 0d)
                Isp = thrust / (fuelFlow * 9.80665d);

        }
        public override double GetEngineTemp() { return temperature; }
        //public override double GetArea() { return something_based_on_engine_supercharger; }
        public override double GetEmissive() { return fxPower; }
        public override float GetFXPower() { return fxPower; }
        public override float GetFXRunning() { return fxPower; }
        public override float GetFXThrottle() { return fxPower; }
        public override float GetFXSpool() { return fxPower; }
        public override bool GetRunning() { return combusting; }
    }
}
