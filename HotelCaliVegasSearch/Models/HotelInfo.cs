using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace HotelCaliVegasSearch.Models
{

    public class HotelInfoList
    {
        public List<HotelInfo> Hotels { get; set; }
    }

    public class HotelInfo
    {
        [JsonProperty(PropertyName = "Location Name")]
        public string LocationName { get; set; }
        public string Address { get; set; }
        public string City { get; set; }
        public string State { get; set; }
        public object Zip { get; set; }
        public string Location { get; set; }
    }
}
