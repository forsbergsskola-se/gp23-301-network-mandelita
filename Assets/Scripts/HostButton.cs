using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using TMPro;
using UnityEngine;

public class HostButton : MonoBehaviour
{
    public TMP_Text hostIPLabel;
   
    void Start()
    {
        hostIPLabel.text = Dns.GetHostEntry(Dns.GetHostName()).AddressList.
            First().
            ToString();
    }

    public void OnButtonClick()
    {
        GameSession.HostGame();
    }
   
}
