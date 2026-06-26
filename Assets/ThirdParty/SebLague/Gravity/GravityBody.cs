using UnityEngine;
using System.Collections;

[RequireComponent (typeof (Rigidbody))]
public class GravityBody : MonoBehaviour {
	
	GravityAttractor planet;
	Rigidbody rb;
	
	void Awake () {
		GameObject planetObj = GameObject.FindGameObjectWithTag("Planet");
		if (planetObj == null) {
			Debug.LogError("GravityBody: No GameObject with tag 'Planet' found!");
			enabled = false;
			return;
		}
		planet = planetObj.GetComponent<GravityAttractor>();
		rb = GetComponent<Rigidbody>();

		// Disable rigidbody gravity and rotation as this is simulated in GravityAttractor script
		rb.useGravity = false;
		rb.constraints = RigidbodyConstraints.FreezeRotation;
	}
	
	void FixedUpdate () {
		if (planet == null || rb == null) return;
		// Allow this body to be influenced by planet's gravity
		planet.Attract(rb);
	}
}