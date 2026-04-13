using System;
using System.Collections.Generic;

[Serializable]
public class BoardState
{
    public string playmatPath;
    public List<CounterState> counters = new List<CounterState>();
    public List<DiceState> diceResults = new List<DiceState>();
    public string selectedCounterId;
}