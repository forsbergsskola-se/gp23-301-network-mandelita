using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class JoinButton : MonoBehaviour
{
    public TMP_InputField joinText;

    public void OnButtonClick()
    {
        //GameSession.JoinGame(joinText.text);
        GameSession.JoinGame("127.0.0.1");
    }
}
