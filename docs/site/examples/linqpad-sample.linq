<Query Kind="Statements">
  <Namespace>System.Globalization</Namespace>
</Query>

var culture = CultureInfo.InvariantCulture;
var prices = new[] { 9.99m, 24.50m, 3.25m };

prices
    .Select(p => new { Price = p, Text = p.ToString("N2", culture) })
    .Dump("Formatted prices");
