The purpose of this project is to provide a RavenDB driver that handles PoucDB requests and serves as the persisted store.
The project consinsts 2 parts: one is the UI and the other is the server that represent the RavenDB server. 

Instructions:

1. Open controller 'ValuesController' and run it (IIS Express). 
2. In cmd, open a python server for PouchDB in its folder. CMD Command: 'python -m SimpleHTTPServer'. 
3. In browser go to url localhost:8000/PouchDB and localhost:8000:RavenDB (port 8000 is the default). RavenDB.html is the UI replacing the Raven studio.
4. In the controller, go to the method 'SetHeaders' and make sure that 'Access-Control-Allow-Origin' is set to your PouchDB port.
5. In 'PouchDB.html' make sure the variable 'ravenReference' is set to your driver url.