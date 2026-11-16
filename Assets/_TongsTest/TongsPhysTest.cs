using System;
using System.Collections;
using UnityEngine;

// TEMPORARY test fixture (deleted after validation). Uses a kinematic stand-in hook and the real
// JawTongsCraneLiftingPoint.AttachToHook / DetachFromHook code paths.
public class TongsPhysTest : MonoBehaviour
{
    Sefpro.JawTongs.JawTongsCraneLiftingPoint lp; Obi.ObiRope rope; Transform ring;
    Rigidbody L, R; HingeJoint hinge; Transform shL, shR, foot1, foot2;
    Rigidbody hook; Rigidbody load; GameObject table;
    const string file = "Temp/phystest.log";
    float maxArmSpeed, maxArmAng, maxChainGap, maxRingGap;
    void Log(string s) { System.IO.File.AppendAllText(file, s + "\n"); }

    string Snap(string label)
    {
        var p0 = rope.GetParticlePosition(rope.solverIndices[0]); var p24 = rope.GetParticlePosition(rope.solverIndices[24]); var p12 = rope.GetParticlePosition(rope.solverIndices[12]);
        float gA = Mathf.Min(Vector3.Distance(p0, shR.position), Vector3.Distance(p0, shL.position));
        float gB = Mathf.Min(Vector3.Distance(p24, shR.position), Vector3.Distance(p24, shL.position));
        maxChainGap = Mathf.Max(maxChainGap, gA, gB);
        float spd = Mathf.Max(L.linearVelocity.magnitude, R.linearVelocity.magnitude), ang = Mathf.Max(L.angularVelocity.magnitude, R.angularVelocity.magnitude);
        maxArmSpeed = Mathf.Max(maxArmSpeed, spd); maxArmAng = Mathf.Max(maxArmAng, ang);
        string s = $"[{label}] t={Time.time:F1} hook={(hook != null ? hook.position.ToString("F2") : "-")} ring={ring.position.ToString("F2")} p12={p12.ToString("F2")} pivot={((L.position + R.position) * 0.5f).ToString("F2")} chainEndGap={gA:F3}/{gB:F3} hinge={hinge.angle:F1} footSpan={Mathf.Abs(foot1.position.x - foot2.position.x):F3} footY={foot1.position.y:F2} armSpd={spd:F2} armAng={ang:F2} attached={lp.IsAttached}";
        foreach (var arm in new[] { L, R }) foreach (var j in arm.GetComponents<ConfigurableJoint>()) { var wa = arm.transform.TransformPoint(j.anchor); var ca = j.connectedBody != null ? j.connectedBody.transform.TransformPoint(j.connectedAnchor) : Vector3.zero; s += $" leg[{arm.name.Substring(8,1)}] d={Vector3.Distance(wa, ca):F3}/lim={j.linearLimit.limit:F3}"; }
        if (load != null) s += $" load={load.position.ToString("F2")} loadSpd={load.linearVelocity.magnitude:F2}";
        Log(s); return s;
    }

    IEnumerator MoveHook(Vector3 delta, float speed, float accel = 0.4f)
    {
        Vector3 dir = delta.normalized; float dist = delta.magnitude, travelled = 0, v = 0; float nextS = 0;
        while (travelled < dist - 1e-4f)
        {
            float remaining = dist - travelled; float vmax = Mathf.Min(speed, Mathf.Sqrt(2 * accel * remaining));
            v = Mathf.MoveTowards(v, vmax, accel * Time.fixedDeltaTime);
            float step = Mathf.Min(v * Time.fixedDeltaTime, remaining); hook.MovePosition(hook.position + dir * step); travelled += step;
            if (Time.time > nextS) { Snap("move"); nextS = Time.time + 2f; }
            yield return new WaitForFixedUpdate();
        }
    }

    void MakeHook()
    {
        var go = new GameObject("TestHook"); hook = go.AddComponent<Rigidbody>(); hook.isKinematic = true; hook.useGravity = false; hook.mass = 5; go.AddComponent<SphereCollider>().radius = 0.02f;
        var seat = lp.RingSeatWorldPosition; go.transform.position = seat + Vector3.up * lp.hookSaddleOffset; hook.position = go.transform.position;
    }

    IEnumerator LiftCycle(string tag, float height, float horizontal)
    {
        yield return MoveHook(Vector3.up * height, 0.2f);
        yield return new WaitForSeconds(2f); Snap(tag + "-top");
        if (horizontal > 0) { yield return MoveHook(Vector3.right * horizontal, 0.3f); Snap(tag + "-moved"); yield return new WaitForSeconds(0.5f); Snap(tag + "-stop+0.5"); yield return new WaitForSeconds(3f); Snap(tag + "-stop+3.5"); yield return MoveHook(Vector3.left * horizontal, 0.3f); yield return new WaitForSeconds(2f); }
        yield return MoveHook(Vector3.down * height, 0.2f);
        yield return new WaitForSeconds(2f); Snap(tag + "-lowered");
    }

    IEnumerator Run()
    {
        System.IO.File.WriteAllText(file, "");
        lp = FindAnyObjectByType<Sefpro.JawTongs.JawTongsCraneLiftingPoint>(); rope = GameObject.Find("JawTongs_ChainRope").GetComponent<Obi.ObiRope>(); ring = GameObject.Find("Ring_Mesh").transform;
        L = lp.leftArmBody; R = lp.rightArmBody; hinge = R.GetComponent<HingeJoint>();
        shL = GameObject.Find("LeftShackle_ObiAnchor").transform; shR = GameObject.Find("RightShackle_ObiAnchor").transform;
        foot1 = GameObject.Find("Collider_LeftJawFoot").transform; foot2 = GameObject.Find("Collider_RightJawFoot").transform;
        yield return new WaitForSeconds(2f); Snap("init");
        foreach (var mb in FindObjectsByType<Sefpro.DesktopTesting.DesktopInteractionManager>(FindObjectsSortMode.None)) mb.enabled = false; // keep stray mouse clicks from toggling the hook during the test
        foreach (var mb in FindObjectsByType<NL_AttachCargo>(FindObjectsSortMode.None)) mb.enabled = false;
        Log("chainPath=" + (lp.ringMesh != null && lp.chainRope != null));

        // ---- Test A/B/F: unloaded, three cycles
        MakeHook(); bool ok = lp.AttachToHook(hook.transform, hook, true); Log("attach=" + ok); yield return new WaitForSeconds(1.5f); Snap("attached");
        maxArmSpeed = maxArmAng = maxChainGap = 0;
        for (int c = 0; c < 1; c++) { yield return LiftCycle("cycle" + c, 1.4f, c == 0 ? 0.6f : 0f); Log($"cycle{c} stats maxArmSpd={maxArmSpeed:F2} maxArmAng={maxArmAng:F2} maxChainGap={maxChainGap:F3}"); }
        lp.DetachFromHook(); yield return new WaitForSeconds(2f); Snap("detached");
        Destroy(hook.gameObject); hook = null;

        // ---- Test C: jaw open/close with torque on both arms (tongs hanging free on stand, unhooked)
        Log("TEST C: hinge range via torque");
        float minH = 999, maxH = -999; float t = 0;
        while (t < 3f) { L.AddTorque(Vector3.forward * 40f); R.AddTorque(Vector3.back * 40f); minH = Mathf.Min(minH, hinge.angle); maxH = Mathf.Max(maxH, hinge.angle); t += Time.fixedDeltaTime; yield return new WaitForFixedUpdate(); }
        Snap("torqueA"); t = 0;
        while (t < 3f) { L.AddTorque(Vector3.back * 40f); R.AddTorque(Vector3.forward * 40f); minH = Mathf.Min(minH, hinge.angle); maxH = Mathf.Max(maxH, hinge.angle); t += Time.fixedDeltaTime; yield return new WaitForFixedUpdate(); }
        Snap("torqueB"); Log($"hinge observed range {minH:F1}..{maxH:F1}"); yield return new WaitForSeconds(2f); Snap("C-end");

        Log("done");
    }
    IEnumerator Safe() { IEnumerator r = Run(); while (true) { try { if (!r.MoveNext()) yield break; } catch (Exception e) { Log("EXCEPTION " + e); yield break; } yield return r.Current; } }
    void Start() { StartCoroutine(Safe()); }
}



