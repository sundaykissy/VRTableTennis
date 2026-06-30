using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class playerside : MonoBehaviour {

    void Start () {}
    void Update () {}

    void OnCollisionEnter(Collision collision)
    {
        // Paddle touching player side is physics noise — no log, no state change.
        if (collision.gameObject.name == "Paddle") return;
    }
}
