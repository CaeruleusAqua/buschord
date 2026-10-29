// Same independently signed synthetic export as ProtectedKnxKeyringChecks; no installation data.
internal static class SecureGroupKeyringFixture
{
    internal const string Rich = """
        <Keyring xmlns="http://knx.org/xml/keyring/1" Project="BusChord synthetic keyring" CreatedBy="Synthetic vector" Created="2019-06-11T06:45:22" Signature="O6g27O2hyVUL9PI0O3E8zA==">
          <Backbone MulticastAddress="224.0.23.12" Latency="1000" Key="VGnz2DbdiMqN5EE4I7tqLw==" />
          <Interface Type="Tunneling" Host="1.2.0" UserID="4" IndividualAddress="1.2.10" Password="DQJOj7CrytX+4EV27SINp3xXa7HEfJQu+XiCt9U5AU8=" Authentication="//pj5KplK+4H7gcYVefuuCumcPVI0BBR/MXfFtaat2k=">
            <Group Address="4660" Senders="1.2.1 1.2.2" />
          </Interface>
          <Interface Type="USB" IndividualAddress="1.2.11"><Group Address="4660" Senders="1.2.2" /></Interface>
          <Interface Type="Backbone" IndividualAddress="1.2.12"><Group Address="17185" /></Interface>
          <GroupAddresses><Group Address="4660" Key="iA2KpI19ZlW0jseoXSycAg==" /><Group Address="17185" Key="pvyOR3j9CpJw9R2/WWVCZQ==" /></GroupAddresses>
          <Devices><Device IndividualAddress="1.2.0" ToolKey="GI0PDDbpgQUZK2JkTvR8pA==" ManagementPassword="ezGR1sf4vsWKWS8JMIJuzmrtEG2N9WLdyTDrwIZbNuM=" Authentication="qEUJHMW48qXne6MySIUPlWwd7RHLmh6gnS/EIzaTIew=" SequenceNumber="108" /></Devices>
        </Keyring>
        """;
}
