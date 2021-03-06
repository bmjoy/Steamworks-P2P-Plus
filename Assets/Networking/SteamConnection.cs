﻿using Facepunch.Steamworks;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Not actually a connection, just the node of the connection (endpoint, or us)
/// </summary>
[System.Serializable]
public class SteamConnection {
    public ulong steamID;
    public int connectionIndex = 0; //lower authority, the better.  0 is "master" authority.

    public Networking.SteamP2PSessionState connectionState;
    public int ping = 0;//in ms
    public List<float> openPings = new List<float>();
    public float connectionEstablishedTime = 0f;
    public float timeSinceLastMsg = 0f; //send or rec
    //we want the connection state here too... 
    //how do we get that... 
    
    //Returns true if you have higher auth than c, this means you're responisible for sending data to them
    //like state data or keep alives or whatever
    public bool HasAuthOver(SteamConnection c) {
        return connectionIndex < c.connectionIndex;
    }

    //starts the ping->pong->pung sequence so we can calculate ping
    //on both sides between this connection and me
    public void Ping() {
        NetworkManager.instance.SendMessage(steamID, "Ping");
        openPings.Add(Time.realtimeSinceStartup);
    }
}

